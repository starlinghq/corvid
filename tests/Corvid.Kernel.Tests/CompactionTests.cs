using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Context;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>
/// A summarizer slot. Every call is recorded; the reply is a JSON record built from the request's header by default
/// (<c>Events 1–8</c>, <c>part 2 of 5</c>), so merged records show which chunk said what.
/// </summary>
internal sealed partial class FakeSummarizer(Func<string, string>? reply = null) : IChatClient
{
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    /// <summary>The first user message of each call: the header and the events.</summary>
    public IEnumerable<string> Contents => Calls.Select(call => call.Messages[1].Text);

    public static string Record(string content) =>
        $$"""{"goal":null,"constraints":[],"decisions":["{{HeaderOf(content)}}"],"files":[],"open_questions":[],"next_step":"{{HeaderOf(content)}}"}""";

    public static string HeaderOf(string content) => HeaderLine().Match(content).Groups[1].Value;

    [GeneratedRegex("^Events ([^:]*):")]
    private static partial Regex HeaderLine();

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (Calls)
            Calls.Add((list, options));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, (reply ?? Record)(list[1].Text))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

public sealed class CompactionTests
{
    // chars / 4 tokens: a 3,000-token generator leaves 3,000 - 1,024 - 256 = 1,720 for the prompt, and a record 430.
    private const int SmallContext = 3_000;
    private static readonly string A = Long('a'), B = Long('b'), C = Long('c');

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // No tools on offer, so the budget arithmetic is the history's alone.
    private static readonly TurnOptions NoTools = new() { ToolFilter = _ => false };

    private static string Long(char letter) => string.Concat(Enumerable.Repeat($"{letter}{letter}{letter} section text, ", 160));

    /// <summary>Goal, todos and three answered turns: about 1,850 tokens, over the small generator's budget.</summary>
    private static long SeedHistory(TurnHarness harness) => harness.Seed(
        new GoalSet("ship the report"),                                                      // 1
        new TodosSet([new TodoItem("t1", "write intro", false), new TodoItem("t2", "proofread", true)]), // 2
        new UserMessage("draft section one"), new AssistantMessage(A),                     // 3-4
        new UserMessage("draft section two"), new AssistantMessage(B),                     // 5-6
        new UserMessage("draft section three"), new AssistantMessage(C));                  // 7-8

    private static TurnHarness Harness(FakeGenerator generator, FakeSummarizer? summarizer = null, int summarizerContext = 8_192,
        FakeSummarizer? summarizerEscalation = null, FakeGenerator? escalation = null, TurnOptions? options = null) =>
        new(generator, escalation: escalation, options: options ?? NoTools, generatorContext: SmallContext, summarizer: summarizer,
            summarizerContext: summarizerContext, summarizerEscalation: summarizerEscalation);

    private static IReadOnlyList<Compacted> Compactions(TurnHarness harness) => [.. harness.Payloads().OfType<Compacted>()];

    [Fact]
    public async Task A_range_that_fits_the_summarizer_is_compacted_in_one_call_before_the_generator_call()
    {
        var summarizer = new FakeSummarizer();
        var generator = new FakeGenerator(Reply.Say("Next: section four."));
        using var harness = Harness(generator, summarizer);
        SeedHistory(harness);

        var result = await harness.RunAsync("what is next?");

        Assert.Equal(TurnStatus.Answered, result.Status);
        var content = Assert.Single(summarizer.Contents);
        Assert.StartsWith("Events 1–8:\n", content, StringComparison.Ordinal);
        Assert.Contains("[4] assistant: " + A, content, StringComparison.Ordinal);
        Assert.Contains("[7] user: draft section three", content, StringComparison.Ordinal);
        // A summarizer writes a record and is offered nothing to call.
        Assert.Equal(ContextCompactor.Instructions, summarizer.Calls[0].Messages[0].Text);
        Assert.Null(summarizer.Calls[0].Options?.Tools);

        var compacted = Assert.Single(Compactions(harness));
        Assert.Equal((1L, 8L, "summarizer", 1), (compacted.FromSequence, compacted.ThroughSequence, compacted.Summarizer, compacted.Chunks));
        Assert.Equal(["1–8"], compacted.Summary.Decisions);
        Assert.Equal(["compacted events 1–8 by summarizer in 1 chunk(s)"], result.Compactions);

        var prompt = Assert.Single(generator.Calls).AllText;
        Assert.Contains("[summary of events 1–8]", prompt, StringComparison.Ordinal);
        Assert.Contains("what is next?", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("aaa section", prompt, StringComparison.Ordinal);
        // Lineage: the raw range is still in the log, and the expanded view still shows it.
        Assert.Contains(SessionProjection.Load(harness.Log, TurnHarness.Session, expandCompactions: true, cancellationToken: Ct).Transcript,
            entry => entry.Content is AssistantMessage { Text: var text } && text == A);
    }

    [Fact]
    public async Task A_range_larger_than_the_summarizer_is_map_reduced_without_dropping_any_text()
    {
        // 1,600 - 768 output - 256 reserve - the instructions leaves about 340 tokens per call: each long answer is split.
        var summarizer = new FakeSummarizer();
        var generator = new FakeGenerator(Reply.Say("Next."));
        using var harness = Harness(generator, summarizer, summarizerContext: 1_600);
        SeedHistory(harness);

        var result = await harness.RunAsync("what is next?");

        Assert.Equal(TurnStatus.Answered, result.Status);
        var contents = summarizer.Contents.ToList();
        Assert.True(contents.Count >= 6, $"{contents.Count} calls");
        Assert.All(contents.Select((content, i) => (content, i)), item =>
            Assert.StartsWith($"Events 1–8, part {item.i + 1} of {contents.Count}:\n", item.content, StringComparison.Ordinal));
        // Every character of every event went to the summarizer: the pieces join back into the whole range.
        var bodies = contents.Select(content => content[(content.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
        var joined = string.Concat(bodies).Replace("\n", "", StringComparison.Ordinal);
        foreach (var text in new[] { "[3] user: draft section one", "[4] assistant: " + A, "[6] assistant: " + B, "[8] assistant: " + C })
            Assert.Contains(text, joined, StringComparison.Ordinal);

        var compacted = Assert.Single(Compactions(harness));
        Assert.Equal(contents.Count, compacted.Chunks);
        // The merge keeps every chunk's decisions in order and the newest next step.
        Assert.Equal([.. Enumerable.Range(1, contents.Count).Select(i => $"1–8, part {i} of {contents.Count}")], compacted.Summary.Decisions);
        Assert.Equal($"1–8, part {contents.Count} of {contents.Count}", compacted.Summary.NextStep);
    }

    [Fact]
    public async Task A_later_compaction_updates_the_earlier_record_instead_of_rewriting_it()
    {
        var earlier = new CompactionSummary("old goal", ["local only"], ["chose markdown"], ["report.md"], ["which font?"], "old next");
        var summarizer = new FakeSummarizer(_ => """{"goal":null,"decisions":["drafted two sections","chose markdown"],"next_step":"proofread"}""");
        var generator = new FakeGenerator(Reply.Say("Next."));
        using var harness = Harness(generator, summarizer);
        harness.Seed(new UserMessage("start"), new AssistantMessage("started"), new Compacted(1, 2, earlier),   // 1-3
            new UserMessage("draft one"), new AssistantMessage(A), new UserMessage("draft two"), new AssistantMessage(B),
            new UserMessage("draft three"), new AssistantMessage(C));                                         // 4-9

        await harness.RunAsync("what is next?");

        // The summarizer reads only what the earlier record does not cover.
        var content = Assert.Single(summarizer.Contents);
        Assert.StartsWith("Events 1–9:\n[4] user: draft one", content, StringComparison.Ordinal);
        Assert.DoesNotContain("started", content, StringComparison.Ordinal);
        var summary = Compactions(harness)[^1].Summary;
        Assert.Equal((1L, 9L), (Compactions(harness)[^1].FromSequence, Compactions(harness)[^1].ThroughSequence));
        Assert.Equal("old goal", summary.Goal);
        Assert.Equal(["local only"], summary.Constraints);
        Assert.Equal(["chose markdown", "drafted two sections"], summary.Decisions);
        Assert.Equal(["report.md"], summary.Files);
        Assert.Equal(["which font?"], summary.OpenQuestions);
        Assert.Equal("proofread", summary.NextStep);
        Assert.Empty(SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: Ct).Issues);
    }

    [Fact]
    public async Task A_record_over_its_budget_escalates_the_summarizer()
    {
        var oversized = new FakeSummarizer(_ => $$"""{"decisions":["{{new string('x', 2_400)}}"]}""");
        var larger = new FakeSummarizer();
        var generator = new FakeGenerator(Reply.Say("Next."));
        using var harness = Harness(generator, oversized, summarizerEscalation: larger);
        SeedHistory(harness);

        var result = await harness.RunAsync("what is next?");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Single(oversized.Calls);
        Assert.Single(larger.Calls);
        var compacted = Assert.Single(Compactions(harness));
        Assert.Equal("summarizer-large", compacted.Summarizer);
        var switches = harness.Payloads().OfType<ModelSwitched>().Where(item => item.Slot == ModelSlots.Summarizer).ToList();
        // Escalated for the compaction, then back to the default when the turn finished.
        Assert.Equal(["escalation", "de_escalation"], switches.Select(item => item.Reason));
        Assert.Same(harness.SummarizerBinding, harness.Slots.Current(ModelSlots.Summarizer));
    }

    [Fact]
    public async Task With_nowhere_to_escalate_the_range_is_kept_whole_and_the_turn_fails_saying_so()
    {
        var oversized = new FakeSummarizer(_ => $$"""{"decisions":["{{new string('x', 2_400)}}"]}""");
        var generator = new FakeGenerator(Reply.Say("unreachable"));
        using var harness = Harness(generator, oversized);
        SeedHistory(harness);

        var result = await harness.RunAsync("what is next?");

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Contains("no longer fits", result.Text, StringComparison.Ordinal);
        Assert.Empty(Compactions(harness));
        Assert.Empty(generator.Calls);
        var line = Assert.Single(result.Compactions);
        Assert.StartsWith("kept events 1–8 uncompacted: summarizer: the merged record is ", line, StringComparison.Ordinal);
        Assert.EndsWith("against 430", line, StringComparison.Ordinal);
        Assert.Equal(A, harness.Payloads().OfType<AssistantMessage>().First().Text);
    }

    [Fact]
    public async Task A_summarizer_that_never_writes_a_record_is_asked_once_more_then_nothing_is_appended()
    {
        var chatty = new FakeSummarizer(_ => "Sure! The conversation was about a report.");
        using var harness = Harness(new FakeGenerator(), chatty);
        SeedHistory(harness);

        var result = await harness.RunAsync("what is next?");

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Equal(4, chatty.Calls[1].Messages.Count);
        Assert.Equal(2, chatty.Calls.Count);
        Assert.Empty(Compactions(harness));
        Assert.Contains("did not return a record", Assert.Single(result.Compactions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_summarizer_slot_the_generator_binding_summarizes()
    {
        // The generator's own window takes the range in two calls; every call is answered by the same step.
        Func<GeneratorCall, ChatResponse> step = call => Reply.Text(call.Messages[0].Text == ContextCompactor.Instructions
            ? FakeSummarizer.Record(call.Messages[1].Text) : "Next.");
        var generator = new FakeGenerator([.. Enumerable.Repeat(step, 8)]);
        using var harness = Harness(generator);
        SeedHistory(harness);

        var result = await harness.RunAsync("what is next?");

        Assert.Equal("Next.", result.Text);
        Assert.Equal("gen-small", Assert.Single(Compactions(harness)).Summarizer);
        Assert.Equal(ContextCompactor.Instructions, generator.Calls[0].Messages[0].Text);
        Assert.Null(generator.Calls[0].Options?.Tools);
        Assert.Contains("[summary of events 1–8]", generator.Calls[^1].AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Goal_and_todos_are_reinjected_after_every_compaction()
    {
        var generator = new FakeGenerator(Reply.Say("Next."), Reply.Say("Still next."));
        using var harness = Harness(generator, new FakeSummarizer());
        SeedHistory(harness);
        var runner = harness.Runner();

        await harness.RunAsync(runner, "what is next?");
        harness.Seed(new UserMessage("a long aside"), new AssistantMessage(A), new UserMessage("another"), new AssistantMessage(B),
            new AssistantMessage(C), new GoalSet("ship the report today"));
        await harness.RunAsync(runner, "and now?");

        Assert.Equal(2, Compactions(harness).Count);
        foreach (var (call, goal) in generator.Calls.Zip(["ship the report", "ship the report today"]))
        {
            var texts = call.Messages.Select(message => message.Text).ToList();
            var summary = texts.FindLastIndex(text => text.StartsWith("[summary of events", StringComparison.Ordinal));
            Assert.True(summary > 0);
            Assert.Equal($"[still in force after the summary]\ngoal: {goal}\ntodo [ ] write intro\ntodo [x] proofread", texts[summary + 1]);
        }
    }

    [Fact]
    public async Task Earlier_rounds_of_a_long_turn_are_compacted_sparing_the_request_and_the_latest_round()
    {
        var argument = new string('q', 2_400);
        var lookup = TestTool.Create("lookup", "Look something up.", new ToolRisk(SideEffects.None, Network: false, Destructive: false),
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""",
            (_, _) => ValueTask.FromResult<object?>("found"), new ToolParameter("query", null, true, false));
        var call = $$"""{"query":"{{argument}}"}""";
        var generator = new FakeGenerator(Reply.Call("lookup", call, "l1"), Reply.Call("lookup", call, "l2"), Reply.Call("lookup", call, "l3"),
            Reply.Say("Found it."));
        using var harness = new TurnHarness(generator, options: new TurnOptions { ToolFilter = tool => tool.Name == "lookup" },
            generatorContext: SmallContext, summarizer: new FakeSummarizer(), tools: tools => tools.Add(lookup));

        var result = await harness.RunAsync("look it up");

        Assert.Equal(TurnStatus.Answered, result.Status);
        var events = harness.Events();
        var third = events.Single(item => item.Payload is ToolCallIssued { CallId: "l3" }).Sequence;
        var compacted = Assert.Single(Compactions(harness));
        Assert.Equal(2L, compacted.FromSequence);
        Assert.True(compacted.ThroughSequence < third);
        var prompt = generator.Calls[^1];
        Assert.Equal("look it up", prompt.Messages[1].Text);
        Assert.StartsWith("[summary of events 2–", prompt.Messages[2].Text, StringComparison.Ordinal);
        Assert.Contains(prompt.Messages, message => message.Contents.OfType<FunctionCallContent>().Any(content => content.CallId == "l3"));
        Assert.Empty(SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: Ct).Issues);
    }

    [Fact]
    public async Task A_switch_to_a_smaller_window_compacts_before_the_next_call()
    {
        var generator = new FakeGenerator(Reply.Say("small answered"));
        var large = new FakeGenerator(Reply.Say("large answered"));
        using var harness = Harness(generator, new FakeSummarizer(), escalation: large);
        SeedHistory(harness);
        var runner = harness.Runner();
        await harness.Slots.SwitchAsync(ModelSlots.Generator, harness.EscalationBinding!, SwitchReason.User, pin: true, Ct);

        await harness.RunAsync(runner, "first, on the large model");
        Assert.Empty(Compactions(harness));
        Assert.Contains("aaa section", large.Calls[0].AllText, StringComparison.Ordinal);

        var back = await harness.Slots.SwitchAsync(ModelSlots.Generator, harness.GeneratorBinding, SwitchReason.User, cancellationToken: Ct);
        Assert.Equal(BudgetAction.CompactBeforeNextCall, back.Budget);
        runner.NoteBudgetChange(back);
        await harness.RunAsync(runner, "now on the small one");

        var compacted = harness.Events().Single(item => item.Payload is Compacted);
        Assert.True(compacted.Sequence > back.Sequence);
        var range = (Compacted)compacted.Payload;
        Assert.Equal(1L, range.FromSequence);
        Assert.Contains("[summary of events 1–", Assert.Single(generator.Calls).AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_switch_to_a_larger_window_expands_compacted_ranges_and_a_switch_back_compacts_again()
    {
        var generator = new FakeGenerator(Reply.Say("one"), Reply.Say("three"));
        var large = new FakeGenerator(Reply.Say("two"));
        using var harness = Harness(generator, new FakeSummarizer(), escalation: large);
        SeedHistory(harness);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);

        await actor.SubmitAsync(new Envelope(TurnHarness.Session, "cli", "what is next?"), Ct);
        await sink.NextAsync();
        var first = Assert.Single(Compactions(harness));

        var up = await actor.SwitchModelAsync(ModelSlots.Generator, harness.EscalationBinding!, pin: true, Ct);
        Assert.Equal(BudgetAction.ExpandCompactedRanges, up.Budget);
        await actor.SubmitAsync(new Envelope(TurnHarness.Session, "cli", "show me everything"), Ct);
        Assert.Equal("two", (await sink.NextAsync()).Result!.Text);

        var expansion = (CompactionExpanded)harness.Payloads().Single(item => item is CompactionExpanded);
        Assert.Equal(harness.Events().First(item => item.Payload is Compacted).Sequence, expansion.CompactedSequence);
        var prompt = Assert.Single(large.Calls).AllText;
        Assert.Contains("aaa section", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[summary of events", prompt, StringComparison.Ordinal);

        await actor.SwitchModelAsync(ModelSlots.Generator, harness.GeneratorBinding, cancellationToken: Ct);
        await actor.SubmitAsync(new Envelope(TurnHarness.Session, "cli", "and on the small one?"), Ct);
        Assert.Equal("three", (await sink.NextAsync()).Result!.Text);

        var second = Compactions(harness)[^1];
        Assert.Equal(2, Compactions(harness).Count);
        Assert.Equal(1L, second.FromSequence);
        Assert.True(second.ThroughSequence > first.ThroughSequence);
        Assert.Contains("[summary of events 1–", generator.Calls[^1].AllText, StringComparison.Ordinal);
        Assert.Empty(SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: Ct).Issues);
    }

    [Fact]
    public void Expansion_stops_where_the_budget_does_not_allow()
    {
        using var harness = Harness(new FakeGenerator());
        SeedHistory(harness);
        harness.Seed(new Compacted(1, 8, CompactionRecords.Empty));
        var compactor = new ContextCompactor(harness.Log, harness.Slots);
        RenderedPrompt Render(SessionSnapshot snapshot) => PromptRenderer.Render(snapshot, "system", CharsPerTokenCounter.Instance, 10_000);

        Assert.Equal(0, compactor.Expand(Render, 1_000, Ct));
        Assert.Equal(1, compactor.Expand(Render, 5_000, Ct));
        Assert.Equal(0, compactor.Expand(Render, 5_000, Ct));
        Assert.IsType<CompactionExpanded>(harness.Payloads()[^1]);
    }

    [Fact]
    public void Ranges_spare_the_turns_message_and_its_latest_round_and_skip_a_lone_summary()
    {
        var snapshot = SessionProjection.Build("s", Stored(
            new UserMessage("old"), new AssistantMessage("old answer"),                   // 1-2
            new UserMessage("now"),                                                       // 3
            new ToolCallIssued("c1", "t", Samples.Json("{}")), new ToolResult("c1", Samples.Json("{}")), // 4-5
            new ToolCallIssued("c2", "t", Samples.Json("{}")), new ToolResult("c2", Samples.Json("{}")))); // 6-7

        Assert.Equal([new CompactionRange(1, 2)], ContextCompactor.Ranges(snapshot, 3, null));
        Assert.Equal([new CompactionRange(1, 2), new CompactionRange(4, 5)], ContextCompactor.Ranges(snapshot, 3, 6));
        Assert.Empty(ContextCompactor.Ranges(snapshot, 1, null));

        var compacted = SessionProjection.Build("s", Stored(new UserMessage("old"), new AssistantMessage("x"),
            new Compacted(1, 2, CompactionRecords.Empty), new UserMessage("now")));
        Assert.Empty(ContextCompactor.Ranges(compacted, 4, null));
    }

    private static IEnumerable<StoredEvent> Stored(params SessionEvent[] events) =>
        events.Select((payload, i) => new StoredEvent("s", i + 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, payload));

    [Fact]
    public void Chunks_never_exceed_their_room_and_split_lines_join_back_whole()
    {
        string[] lines = ["short", new string('x', 1_000) + "😀" + new string('y', 1_001), "tail"];

        var chunks = ContextCompactor.Chunk(lines, CharsPerTokenCounter.Instance, 100);

        Assert.All(chunks, chunk => Assert.True(CharsPerTokenCounter.Instance.CountTokens(chunk) <= 100));
        Assert.Equal(string.Concat(lines), string.Concat(chunks).Replace("\n", "", StringComparison.Ordinal));
        Assert.DoesNotContain(chunks, chunk => char.IsHighSurrogate(chunk.TrimEnd('\n')[^1]));
    }

    [Theory]
    [InlineData("""{"goal":"g","constraints":["c"],"decisions":"one","files":null,"open_questions":[],"next_step":"n"}""", "g", "c", "one", "n")]
    [InlineData("Here it is:\n```json\n{\"goal\": \"g\", \"decisions\": [\"d\", \"d\", \" \"],}\n```", "g", "", "d", null)]
    public void Records_are_read_leniently(string reply, string goal, string constraint, string decision, string? next)
    {
        var record = CompactionRecords.Parse(reply)!;

        Assert.Equal(goal, record.Goal);
        Assert.Equal(constraint.Length == 0 ? [] : [constraint], record.Constraints);
        Assert.Equal([decision], record.Decisions);
        Assert.Empty(record.Files);
        Assert.Equal(next, record.NextStep);
    }

    [Theory]
    [InlineData("no JSON at all")]
    [InlineData("""{"answer": 42}""")]
    [InlineData("""["goal"]""")]
    [InlineData("""{"goal": "unterminated""")]
    public void Replies_without_a_record_are_refused(string reply) => Assert.Null(CompactionRecords.Parse(reply));

    [Fact]
    public void Merging_keeps_every_item_once_in_order_and_the_newest_goal_and_next_step()
    {
        var merged = CompactionRecords.Merge(
        [
            new CompactionSummary("first goal", ["a"], ["x"], ["f1"], ["q1"], "step one"),
            new CompactionSummary(null, ["b", "a"], [], ["f2"], [], null),
            new CompactionSummary("second goal", [], ["y", "x"], [], ["q2"], "step two"),
        ]);

        Assert.Equal("second goal", merged.Goal);
        Assert.Equal(["a", "b"], merged.Constraints);
        Assert.Equal(["x", "y"], merged.Decisions);
        Assert.Equal(["f1", "f2"], merged.Files);
        Assert.Equal(["q1", "q2"], merged.OpenQuestions);
        Assert.Equal("step two", merged.NextStep);
        Assert.Equal(merged.Decisions, CompactionRecords.Parse(CompactionRecords.ToJson(merged))!.Decisions);
    }
}
