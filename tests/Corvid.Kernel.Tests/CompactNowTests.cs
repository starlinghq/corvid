using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Context;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary><c>/compact</c>'s kernel half (C-035): <see cref="TurnRunner.CompactNowAsync"/> by the turn loop's own rule.</summary>
public sealed class CompactNowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TurnOptions NoTools = new() { ToolFilter = _ => false };

    private static TurnHarness Harness(FakeSummarizer summarizer) =>
        new(new FakeGenerator(), options: NoTools, summarizer: summarizer);

    [Fact]
    public async Task Everything_before_the_latest_turn_is_compacted_and_the_latest_turn_stays()
    {
        var summarizer = new FakeSummarizer();
        using var harness = Harness(summarizer);
        harness.Seed(
            new UserMessage("draft section one"), new AssistantMessage("one drafted"),   // 1-2
            new UserMessage("draft section two"), new AssistantMessage("two drafted"),   // 3-4
            new UserMessage("draft section three"), new AssistantMessage("three drafted")); // 5-6

        var result = await harness.Runner().CompactNowAsync(Ct);

        Assert.Equal((CompactionStatus.Compacted, 1L, 4L), (result.Status, result.From, result.Through));
        Assert.Equal("compacted events 1–4 by summarizer in 1 chunk(s)", result.Describe());
        Assert.Equal("1–4", result.Record!.NextStep);
        var content = Assert.Single(summarizer.Contents);
        Assert.DoesNotContain("draft section three", content, StringComparison.Ordinal);

        var transcript = SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: Ct).Transcript;
        Assert.IsType<Compacted>(transcript[0].Content);
        Assert.Equal([5L, 6L], transcript.Skip(1).Select(entry => entry.FromSequence));
        // /undo still finds the latest turn's message.
        Assert.Equal(4L, CheckpointService.PlanUndo(SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: Ct))!.Position);
    }

    [Fact]
    public async Task A_single_turn_or_an_already_compacted_history_has_nothing_to_compact()
    {
        var summarizer = new FakeSummarizer();
        using var harness = Harness(summarizer);
        var runner = harness.Runner();
        Assert.Equal(CompactionStatus.NothingToCompact, (await runner.CompactNowAsync(Ct)).Status);

        harness.Seed(new UserMessage("hello"), new AssistantMessage("hi"));
        Assert.Equal(CompactionStatus.NothingToCompact, (await runner.CompactNowAsync(Ct)).Status);

        harness.Seed(new UserMessage("again"), new AssistantMessage("hi again"));
        Assert.Equal(CompactionStatus.Compacted, (await runner.CompactNowAsync(Ct)).Status);
        // One record standing alone before the latest turn is as short as compaction makes it.
        Assert.Equal(CompactionStatus.NothingToCompact, (await runner.CompactNowAsync(Ct)).Status);
        Assert.Single(summarizer.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pending_expansion_is_dropped_only_when_the_compaction_was_written(bool summaryFits)
    {
        // The summarizer writes records until told to fail, then answers with something that is not a record (Unfit).
        var failing = false;
        var summarizer = new FakeSummarizer(content => failing ? "not a record" : FakeSummarizer.Record(content));
        var large = new FakeGenerator(Reply.Say("on the large model"));
        using var harness = new TurnHarness(new FakeGenerator(), escalation: large, options: NoTools, summarizer: summarizer);
        harness.Seed(
            new UserMessage("draft section one"), new AssistantMessage("one drafted"),   // 1-2
            new UserMessage("draft section two"), new AssistantMessage("two drafted"),   // 3-4
            new UserMessage("draft section three"), new AssistantMessage("three drafted")); // 5-6
        var runner = harness.Runner();
        Assert.Equal(CompactionStatus.Compacted, (await runner.CompactNowAsync(Ct)).Status);

        harness.Seed(new UserMessage("draft section four"), new AssistantMessage("four drafted"));
        var up = await harness.Slots.SwitchAsync(ModelSlots.Generator, harness.EscalationBinding!, SwitchReason.User, pin: true, Ct);
        Assert.Equal(BudgetAction.ExpandCompactedRanges, up.Budget);
        runner.NoteBudgetChange(up);

        failing = !summaryFits;
        var again = await runner.CompactNowAsync(Ct);
        Assert.Equal(summaryFits ? CompactionStatus.Compacted : CompactionStatus.Unfit, again.Status);

        await harness.RunAsync(runner, "show me everything");
        // An unfit summary compacted nothing, so the switch's expansion still runs on the next call; a written one drops it.
        Assert.Equal(summaryFits ? 0 : 1, harness.Payloads().OfType<CompactionExpanded>().Count());
        Assert.Equal(!summaryFits, Assert.Single(large.Calls).AllText.Contains("draft section one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_summarizer_runs_inside_the_session_request_context_with_its_taint()
    {
        var seen = new List<ModelRequestContext?>();
        var summarizer = new FakeSummarizer(content =>
        {
            seen.Add(ModelRequestContext.Current);
            return FakeSummarizer.Record(content);
        });
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("noted"), Reply.Say("noted again")), options: NoTools,
            summarizer: summarizer);
        var runner = harness.Runner();
        await harness.RunAsync(runner, "forwarded: draft section one", EnvelopeOrigin.External);
        await harness.RunAsync(runner, "forwarded: draft section two", EnvelopeOrigin.External);
        Assert.True(runner.Tainted);

        Assert.Equal(CompactionStatus.Compacted, (await runner.CompactNowAsync(Ct)).Status);

        var context = Assert.Single(seen);
        Assert.Equal(new ModelRequestContext(TurnHarness.Session, Tainted: true), context);
        Assert.Null(ModelRequestContext.Current);
    }

    [Fact]
    public async Task A_focus_reaches_every_chunk_and_the_event_and_the_history_shrinks()
    {
        // A small summarizer window, so the range is map-reduced in several chunks (as in CompactionTests).
        var summarizer = new FakeSummarizer();
        using var harness = new TurnHarness(new FakeGenerator(), options: NoTools, summarizer: summarizer, summarizerContext: 1_600);
        var answer = string.Join(" ", Enumerable.Repeat("The quarterly report needs the revenue table, the churn chart and the hiring plan.", 25));
        harness.Seed(
            new UserMessage("draft section one"), new AssistantMessage(answer),     // 1-2
            new UserMessage("draft section two"), new AssistantMessage(answer),     // 3-4
            new UserMessage("draft section three"), new AssistantMessage("ok"));    // 5-6

        var result = await harness.Runner().CompactNowAsync("  the revenue figures for Q3  ", Ct);

        Assert.Equal(CompactionStatus.Compacted, result.Status);
        var contents = summarizer.Contents.ToList();
        Assert.True(contents.Count >= 2, $"{contents.Count} calls");
        // The focus is its own line after each chunk's header, before the events, and the system prompt is unchanged.
        Assert.All(contents, content =>
        {
            var lines = content.Split('\n');
            Assert.StartsWith("Events 1–4", lines[0], StringComparison.Ordinal);
            Assert.Equal("The user asked this summary to keep, in full: the revenue figures for Q3", lines[1]);
            Assert.Single(lines, line => line.StartsWith("The user asked", StringComparison.Ordinal));
        });
        Assert.All(summarizer.Calls, call => Assert.Equal(ContextCompactor.Instructions, call.Messages[0].Text));
        var compacted = Assert.IsType<Compacted>(harness.Payloads().OfType<Compacted>().Single());
        Assert.Equal("the revenue figures for Q3", compacted.Focus);
        Assert.Equal(contents.Count, compacted.Chunks);

        Assert.NotNull(result.PromptTokensBefore);
        Assert.NotNull(result.PromptTokensAfter);
        Assert.True(result.PromptTokensBefore > result.PromptTokensAfter, $"{result.PromptTokensBefore} → {result.PromptTokensAfter}");
        // Describe() feeds the turn loop's notes and the golden trajectories, so it does not change.
        Assert.Equal($"compacted events 1–4 by summarizer in {contents.Count} chunk(s)", result.Describe());
    }

    [Fact]
    public async Task Without_a_focus_the_event_has_none_and_nothing_to_compact_carries_only_the_before_figure()
    {
        var summarizer = new FakeSummarizer();
        using var harness = Harness(summarizer);
        harness.Seed(new UserMessage("hello"), new AssistantMessage("hi"));
        var runner = harness.Runner();

        var nothing = await runner.CompactNowAsync("keep the greeting", Ct);
        Assert.Equal(CompactionStatus.NothingToCompact, nothing.Status);
        Assert.True(nothing.PromptTokensBefore > 0);
        Assert.Null(nothing.PromptTokensAfter);
        Assert.Empty(summarizer.Calls);

        harness.Seed(new UserMessage("again"), new AssistantMessage("hi again"));
        var written = await runner.CompactNowAsync(Ct);
        Assert.Equal(CompactionStatus.Compacted, written.Status);
        Assert.Null(Assert.Single(harness.Payloads().OfType<Compacted>()).Focus);
        Assert.DoesNotContain("The user asked", Assert.Single(summarizer.Contents), StringComparison.Ordinal);
        // An unfocused event keeps C-016's persisted form: no "focus" property.
        var stored = harness.Log.Read(TurnHarness.Session, cancellationToken: Ct).Single(item => item.Payload is Compacted);
        Assert.DoesNotContain("\"focus\"", EventJson.Serialize(stored.Payload), StringComparison.Ordinal);
    }
}
