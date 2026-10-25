using Corvid.Approvals;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Models;

namespace Corvid.Host.Tests;

/// <summary>
/// The session update stream (K-10): a live turn's report and the replay of the same session from the log carry the same
/// prompt, call, result, ledger diff and answer, so every surface draws a loaded session as it drew the live one.
/// </summary>
public sealed class SessionStreamTests
{
    private const string Prompt = "write hello to the file notes.txt";

    private static FakeModels FileWrite()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""), Reply.Say("Wrote it.")));
        // An embedder that ranks write_file first for "write ..." requests, so the shortlist offers it.
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    private static readonly Dictionary<string, string?> FileTools = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
    };

    private sealed class Approve : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) => ValueTask.FromResult(ApprovalAnswer.Approve());
    }

    [Fact]
    public async Task ALiveTurnReportsTheGeneratorsReasoningAndItsTokensOnTheStream()
    {
        var reasoned = new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            [new Microsoft.Extensions.AI.TextReasoningContent("The user wants a greeting."), new Microsoft.Extensions.AI.TextContent("Hello there.")]));
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(_ => reasoned)));
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, new ToolPresentation(runtime), 0, update =>
        {
            lock (live)
                live.Add(update);
        });

        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync("say hello", TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        // K-1: the reasoning before the answer it led to; K-5: the generator call's tokens as the fake provider reports them.
        var thought = Assert.Single(live.OfType<ThoughtChunk>());
        Assert.Equal("The user wants a greeting.", thought.Text);
        Assert.True(live.IndexOf(thought) < live.FindIndex(update => update is AnswerChunk));
        Assert.Equal("Hello there.", string.Concat(live.OfType<AnswerChunk>().Select(chunk => chunk.Text)));
        var usage = Assert.Single(live.OfType<UsageReported>());
        Assert.Equal((42L, 7L), (usage.InputTokens, usage.OutputTokens));
        // Neither is in the log, so a replay has none.
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(replay.Updates, update => update is ThoughtChunk or UsageReported);
    }

    [Fact]
    public async Task ALiveTurnAndItsReplayCarryTheSamePromptCallDiffAndAnswer()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var tools = new ToolPresentation(runtime);
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, tools, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), update =>
        {
            lock (live)
                live.Add(update);
        });

        using (runtime.AttachPrompter(session.Id, new Approve()))
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync(Prompt, TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        var file = Path.Combine(host.Root, "work", "notes.txt");
        var prompt = Assert.IsType<PromptSent>(live[0]);
        Assert.Equal(Prompt, prompt.Text);
        var started = Assert.Single(live.OfType<ToolCallStarted>());
        // It needs approval, so it is announced pending.
        Assert.Equal(("write_file", ToolCallKinds.Edit, ToolCallState.Pending), (started.Tool, started.Kind, started.Status));
        var finished = Assert.Single(live.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Completed, "succeeded", "write_file: notes.txt"), (finished.Status, finished.Outcome, finished.Title));
        Assert.Equal([file], finished.Locations);
        Assert.Equal(new FileDiff(file, null, "hello\n"), Assert.Single(finished.Diffs));
        Assert.Equal("Wrote it.", string.Concat(live.OfType<AnswerChunk>().Select(chunk => chunk.Text)).Trim());
        Assert.Contains(live, update => update is CheckpointLogged { Checkpoint.Kind: "after" });

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), replay.Head);
        var replayed = Assert.IsType<PromptSent>(replay.Updates[0]);
        Assert.Equal(prompt with { At = default }, replayed with { At = default, Generator = null });
        // The replay names the generator the turn ran on (its first decision's TurnState); live, the surface knows its slot.
        Assert.Equal(("fake-gen", (string?)null), (replayed.Generator, prompt.Generator));
        // Each checkpoint's decisions, as the feed reported them live.
        static string Checkpoints(IEnumerable<SessionStreamUpdate> updates) => string.Join(" | ", updates.OfType<DecisionsMade>().Select(made =>
            $"{made.Decisions.Checkpoint} {made.Decisions.Decider}: " + string.Join(", ", made.Decisions.Questions.Select(q => $"{q.Question} {q.Action} {q.Acted}"))));
        Assert.NotEmpty(live.OfType<DecisionsMade>());
        Assert.Equal(Checkpoints(live), Checkpoints(replay.Updates));
        var call = Assert.Single(replay.Updates.OfType<ToolCallStarted>());
        Assert.Equal((started.CallId, "write_file: notes.txt", ToolCallState.Completed), (call.CallId, call.Title, call.Status));
        Assert.Equal("notes.txt", call.Input?.GetProperty("path").GetString());
        var result = Assert.Single(replay.Updates.OfType<ToolCallChanged>());
        Assert.Equal(finished.Diffs, result.Diffs);
        Assert.Equal(finished.OutputText, result.OutputText);
        Assert.Equal("Wrote it.", Assert.Single(replay.Updates.OfType<AnswerChunk>()).Text.Trim());
        Assert.Equal(live.OfType<CheckpointLogged>().Select(item => item.Checkpoint.CheckpointId),
            replay.Updates.OfType<CheckpointLogged>().Select(item => item.Checkpoint.CheckpointId));
        // Every update after the prompt, in the order the log has them.
        Assert.True(replay.Updates.IndexOf(call) < replay.Updates.IndexOf(result));
    }

    [Fact]
    public async Task TheTranscriptReplayIsTheConversationAlone()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using (runtime.AttachPrompter(session.Id, new Approve()))
            await session.SendAsync(Prompt, TestContext.Current.CancellationToken);

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["PromptSent", "ToolCallStarted", "ToolCallChanged", "AnswerChunk"], replay.Updates.Select(update => update.GetType().Name));
        Assert.Empty(Assert.Single(replay.Updates.OfType<ToolCallChanged>()).Diffs);
        Assert.All(replay.Updates, update => Assert.NotEqual(default, update.At));
    }

    [Fact]
    public async Task ACallTheLogHoldsNoResultForReplaysAsCancelledInTheFullReplayOnly()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using var arguments = System.Text.Json.JsonDocument.Parse("""{"path":"notes.txt"}""");
        // The process stopped under the call: the log has the call and no result.
        runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken),
        [
            Corvid.Abstractions.Events.EventData.Create(new Corvid.Abstractions.Events.UserMessage("read my notes")),
            Corvid.Abstractions.Events.EventData.Create(new Corvid.Abstractions.Events.ToolCallIssued("c1", "read_file", arguments.RootElement.Clone())),
        ], TestContext.Current.CancellationToken);
        var tools = new ToolPresentation(runtime);

        var full = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Full, cancellationToken: TestContext.Current.CancellationToken);
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);

        // As a live turn closes a call its interrupt cut: failed, cancelled; never completed.
        Assert.Equal(ToolCallState.Failed, Assert.Single(full.Updates.OfType<ToolCallStarted>()).Status);
        var closed = Assert.Single(full.Updates.OfType<ToolCallChanged>());
        Assert.Equal(("c1", ToolCallState.Failed, "cancelled"), (closed.CallId, closed.Status, closed.Outcome));
        // ACP's session/load (the transcript replay) is unchanged: its recorded exchanges keep such a call as completed.
        Assert.Equal(ToolCallState.Completed, Assert.Single(transcript.Updates.OfType<ToolCallStarted>()).Status);
        Assert.Empty(transcript.Updates.OfType<ToolCallChanged>());
    }

    [Fact]
    public async Task AReplayedCallTookFromItsIssueToItsResultAsTheLiveCardSaid()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var ct = TestContext.Current.CancellationToken;
        var session = runtime.OpenSession(surface: "test");
        using var arguments = System.Text.Json.JsonDocument.Parse("""{"path":"notes.txt"}""");
        var called = runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, ct),
            [Event(new Corvid.Abstractions.Events.UserMessage("read my notes")), Event(new Corvid.Abstractions.Events.ToolCallIssued("c1", "read_file", arguments.RootElement.Clone()))],
            ct)[^1];
        using var output = System.Text.Json.JsonDocument.Parse("""{"content":"hello"}""");
        var answered = runtime.Log.Append(session.Id, called.Sequence, [Event(new Corvid.Abstractions.Events.ToolResult("c1", output.RootElement.Clone()))], ct)[^1];

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: ct);

        // The live card's time is the call's whole wait (an approval included): the log's call to its result.
        var closed = Assert.Single(replay.Updates.OfType<ToolCallChanged>());
        Assert.Equal(answered.RecordedAt - called.RecordedAt, closed.Elapsed);
    }

    private sealed class DenyWithReason(string reason) : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalAnswer.Deny() with { Reason = reason });
    }

    [Fact]
    public async Task AReplayedCallWhoseApprovalWasRefusedIsDeniedWithThePersonsReason()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var tools = new ToolPresentation(runtime);
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, tools, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), update =>
        {
            lock (live)
                live.Add(update);
        });
        using (runtime.AttachPrompter(session.Id, new DenyWithReason(" do not write scripts ")))
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync(Prompt, TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        var finished = Assert.Single(live.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Failed, "denied"), (finished.Status, finished.Outcome));
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        var result = Assert.Single(replay.Updates.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Failed, "denied"), (result.Status, result.Outcome));
        var approval = Assert.IsType<CallApproval>(result.Approval);
        Assert.Equal((false, true, "once", "do not write scripts"), (approval.Approved, approval.Answered, approval.Scope, approval.Note));
        Assert.Equal("user: denied (once)", approval.Reason);

        // The conversation alone (ACP's session/load) stays as it was: a failed call, no approval.
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);
        var plain = Assert.Single(transcript.Updates.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Failed, null, null), (plain.Status, plain.Outcome, plain.Approval));
    }

    private sealed class ApproveForTheSession : IApprovalPrompter
    {
        public int Asked;

        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            return ValueTask.FromResult(ApprovalAnswer.Approve(ApprovalScope.Session));
        }
    }

    [Fact]
    public async Task ACallASessionRuleLetRunUnaskedCarriesTheAssessmentLiveAndOnReplay()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""), Reply.Say("Wrote it."),
            Reply.Call("write_file", """{"path":"notes.txt","content":"hello again\n"}"""), Reply.Say("Wrote it again.")));
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512, tools: ToolCallSupport.None);
        await using var host = TestHost.Create(models, FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var tools = new ToolPresentation(runtime);
        var live = new List<SessionStreamUpdate>();
        var prompter = new ApproveForTheSession();
        using (runtime.AttachPrompter(session.Id, prompter))
        {
            foreach (var text in new[] { Prompt, "write it again" })
            {
                var reporter = new SessionUpdateReporter(runtime, session.Id, tools, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), update =>
                {
                    lock (live)
                        live.Add(update);
                });
                using (runtime.Feed.Subscribe(session.Id, reporter))
                {
                    var delivery = await session.SendAsync(text, TestContext.Current.CancellationToken);
                    await reporter.FinishAsync(delivery.Result?.Text);
                }
            }
        }

        // The first write asked (and wrote a session rule); the second ran under it, unasked.
        Assert.Equal(1, prompter.Asked);
        var finished = live.OfType<ToolCallChanged>().ToList();
        Assert.Equal(2, finished.Count);
        Assert.Null(finished[0].Assessment);
        var assessed = Assert.IsType<Abstractions.Events.ApprovalAssessed>(finished[1].Assessment);
        Assert.Equal((finished[1].CallId, Abstractions.Events.ApprovalResolutions.RuleAllowed, "session"), (assessed.CallId, assessed.Resolution, assessed.RuleScope));
        Assert.StartsWith("`write_file", assessed.Rule, StringComparison.Ordinal);
        Assert.NotNull(finished[1].CheckpointBefore);
        Assert.Equal(false, finished[1].ReadOnly);

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        var replayed = replay.Updates.OfType<ToolCallChanged>().ToList();
        Assert.Equal(finished.Select(item => item.CheckpointBefore), replayed.Select(item => item.CheckpointBefore));
        Assert.NotNull(replayed[0].Approval);
        Assert.Null(replayed[0].Assessment);
        Assert.Equal(Kernel.Events.EventPayload.Json(assessed), Kernel.Events.EventPayload.Json(replayed[1].Assessment!));
        // The conversation alone has none of it.
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(transcript.Updates.OfType<ToolCallChanged>(), item => Assert.Equal((null, null, null), (item.Assessment, item.ReadOnly, item.CheckpointBefore)));
    }

    [Fact]
    public async Task ACallToAReadOnlyToolSaysSoLiveAndOnReplay()
    {
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("No notes."))));
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var tools = new ToolPresentation(runtime);
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, tools, 0, update =>
        {
            lock (live)
                live.Add(update);
        });
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync("list my notes", TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        var finished = Assert.Single(live.OfType<ToolCallChanged>());
        Assert.Equal((true, null), (finished.ReadOnly, finished.Assessment));
        var replayed = Assert.Single((await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken))
            .Updates.OfType<ToolCallChanged>());
        Assert.Equal((true, null), (replayed.ReadOnly, replayed.Assessment));
    }

    /// <summary>A4 review L11: a call id asked in an undone turn does not make the same id's later, unasked call read as asked.</summary>
    [Fact]
    public async Task AnApprovalAskedInAnUndoneTurnDoesNotHideALaterCallsReadOnly()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using var write = System.Text.Json.JsonDocument.Parse("""{"path":"notes.txt","content":"x"}""");
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        using var done = System.Text.Json.JsonDocument.Parse("\"ok\"");
        var head = runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken);
        runtime.Log.Append(session.Id, head,
        [
            Event(new Corvid.Abstractions.Events.UserMessage("write the notes")),
            Event(new Corvid.Abstractions.Events.ToolCallIssued("c1", "write_file", write.RootElement.Clone(), 1, false)),
            Event(new Corvid.Abstractions.Events.ApprovalRequested("a1", "{}", "writes a file", "c1")),
            Event(new Corvid.Abstractions.Events.ApprovalResolved("a1", true, "approved")),
            Event(new Corvid.Abstractions.Events.ToolResult("c1", done.RootElement.Clone())),
            Event(new Corvid.Abstractions.Events.AssistantMessage("Wrote them.")),
            Event(new Corvid.Abstractions.Events.SessionRewound(head, "undo", [])),
            Event(new Corvid.Abstractions.Events.UserMessage("list my notes")),
            Event(new Corvid.Abstractions.Events.ToolCallIssued("c1", "list_notes", empty.RootElement.Clone(), 1, true)),
            Event(new Corvid.Abstractions.Events.ToolResult("c1", done.RootElement.Clone())),
            Event(new Corvid.Abstractions.Events.AssistantMessage("No notes.")),
        ], TestContext.Current.CancellationToken);
        var tools = new ToolPresentation(runtime);

        var replayed = Assert.Single((await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken))
            .Updates.OfType<ToolCallChanged>());

        Assert.Equal((true, null), (replayed.ReadOnly, replayed.Approval));
    }

    private static Corvid.Abstractions.Events.EventData Event(Corvid.Abstractions.Events.SessionEvent payload) =>
        Corvid.Abstractions.Events.EventData.Create(payload);

    [Fact]
    public async Task ANetworkReadTaintsTheReplayUntilTheUsersNextMessage()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using var url = System.Text.Json.JsonDocument.Parse("""{"url":"https://example.org/page"}""");
        using var result = System.Text.Json.JsonDocument.Parse("\"the page\"");
        runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken),
        [
            Event(new Corvid.Abstractions.Events.UserMessage("read the page")),
            Event(new Corvid.Abstractions.Events.ToolCallIssued("c1", "web_fetch", url.RootElement.Clone())),
            Event(new Corvid.Abstractions.Events.ToolResult("c1", result.RootElement.Clone())),
            Event(new Corvid.Abstractions.Events.AssistantMessage("It says hello.")),
            Event(new Corvid.Abstractions.Events.UserMessage("thanks")),
        ], TestContext.Current.CancellationToken);
        var tools = new ToolPresentation(runtime);

        var full = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["PromptSent", "ToolCallStarted", "ToolCallChanged", "TaintNoted", "AnswerChunk", "PromptSent", "TaintNoted"],
            full.Updates.Select(update => update.GetType().Name));
        Assert.Equal([new TaintNoted(true, "web_fetch https://example.org/page"), new TaintNoted(false, null)],
            full.Updates.OfType<TaintNoted>().Select(update => update with { At = default }));
        Assert.Empty(transcript.Updates.OfType<TaintNoted>());
    }

    [Fact]
    public async Task ACompactionReplaysAsItsMarkerAndAnExpansionSaysSo()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var head = runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken);
        var summary = new Corvid.Abstractions.Events.CompactionSummary("ship it", ["keep tests green"], [], ["a.cs"], [], "release");
        runtime.Log.Append(session.Id, head,
        [
            Event(new Corvid.Abstractions.Events.UserMessage("one")),
            Event(new Corvid.Abstractions.Events.AssistantMessage("first")),
            Event(new Corvid.Abstractions.Events.UserMessage("two")),
            Event(new Corvid.Abstractions.Events.AssistantMessage("second")),
        ], TestContext.Current.CancellationToken);
        var compacted = runtime.Log.Append(session.Id, head + 4,
            [Event(new Corvid.Abstractions.Events.Compacted(head + 1, head + 2, summary, Summarizer: "fake-gen", Focus: "the release"))],
            TestContext.Current.CancellationToken)[0];
        var tools = new ToolPresentation(runtime);

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        runtime.Log.Append(session.Id, compacted.Sequence, [Event(new Corvid.Abstractions.Events.CompactionExpanded(compacted.Sequence))],
            TestContext.Current.CancellationToken);
        var expanded = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript, cancellationToken: TestContext.Current.CancellationToken);

        // The conversation is replayed expanded, with the marker after the turns it summarized.
        var marker = Assert.IsType<CompactionLogged>(replay.Updates[^1]);
        Assert.Equal((head + 1, head + 2, "ship it", "the release", false),
            (marker.Compaction.FromSequence, marker.Compaction.ThroughSequence, marker.Compaction.Summary.Goal, marker.Compaction.Focus, marker.Expanded));
        Assert.Equal(4, replay.Updates.Count(update => update is PromptSent or AnswerChunk));
        Assert.True(Assert.Single(expanded.Updates.OfType<CompactionLogged>()).Expanded);
        Assert.Empty(transcript.Updates.OfType<CompactionLogged>());
    }

    [Theory]
    [InlineData("qwen3.5-0.8b local, 8k", "qwen3.5-0.8b")]
    [InlineData("claude-sonnet@2025 cloud, 200k", "claude-sonnet")]
    [InlineData("qwen3.5-0.…", null)]
    [InlineData("", null)]
    public void ATurnStatesGeneratorLineNamesItsModel(string described, string? model) =>
        Assert.Equal(model, SessionReplay.GeneratorName(described));

    [Fact]
    public void ATitleNamesTheTargetOnOneLineAndCutsALongOne()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"command":"echo a\necho b"}""");
        Assert.Equal("shell: echo a echo b", ToolPresentation.Title("shell", document.RootElement));
        Assert.Equal("echo a echo b", ToolPresentation.Target(document.RootElement));
        Assert.Equal("list_notes", ToolPresentation.Title("list_notes", null));
        using var longer = System.Text.Json.JsonDocument.Parse($$"""{"path":"{{new string('a', 300)}}"}""");
        Assert.Equal(160, ToolPresentation.Title("read_file", longer.RootElement).Length);
    }

    [Fact]
    public async Task AReadAfterAPositionCarriesTheStepAndTaintItSkippedAndRepeatsNoQuestionOrDecision()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using var url = System.Text.Json.JsonDocument.Parse("""{"url":"https://example.org/page"}""");
        using var result = System.Text.Json.JsonDocument.Parse("\"the page\"");
        var ct = TestContext.Current.CancellationToken;
        var start = runtime.Log.GetHead(session.Id, ct);
        runtime.Log.Append(session.Id, start,
        [
            Event(new Corvid.Abstractions.Events.UserMessage("read the page")),
            Event(new Corvid.Abstractions.Events.ToolCallIssued("c1", "web_fetch", url.RootElement.Clone(), 1)),
            Event(new Corvid.Abstractions.Events.ToolResult("c1", result.RootElement.Clone())),
        ], ct);
        var first = runtime.Log.GetHead(session.Id, ct);
        // The same step's second call (run after the first), then the answer and its question.
        runtime.Log.Append(session.Id, first,
        [
            Event(new Corvid.Abstractions.Events.ToolCallIssued("c2", "web_fetch", url.RootElement.Clone(), 1)),
            Event(new Corvid.Abstractions.Events.ToolResult("c2", result.RootElement.Clone())),
            Event(new Corvid.Abstractions.Events.AssistantMessage("0.2.0, or 0.2.0-rc1?")),
            Event(new Corvid.Abstractions.Events.ClarificationAsked("q-1", "0.2.0, or 0.2.0-rc1?", ["0.2.0", "0.2.0-rc1"])),
        ], ct);
        var second = runtime.Log.GetHead(session.Id, ct);
        runtime.Log.Append(session.Id, second, [Event(new Corvid.Abstractions.Events.UserMessage("0.2.0"))], ct);
        var tools = new ToolPresentation(runtime);

        var tail = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Full with { After = first }, through: second, cancellationToken: ct);
        var next = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Full with { After = second }, cancellationToken: ct);

        // Step 1 and the taint were reported by the read before: the second call starts no step and taints nothing new.
        Assert.Equal(["ToolCallStarted", "ToolCallChanged", "AnswerChunk", "QuestionRaised"],
            tail.Updates.Where(update => update is not DecisionsMade).Select(update => update.GetType().Name));
        // The next read has the answer, which clears the taint carried from before, and never the question again.
        Assert.Equal(["PromptSent", "TaintNoted"], next.Updates.Select(update => update.GetType().Name));
        Assert.False(next.Updates.OfType<TaintNoted>().Single().Tainted);
    }
}
