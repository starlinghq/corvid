using Corvid.Abstractions.Events;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Context;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;

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
}
