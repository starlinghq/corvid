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
}
