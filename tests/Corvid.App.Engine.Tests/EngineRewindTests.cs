using Corvid.Abstractions.Events;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.History;
using Corvid.App.Engine.Rewind;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Tests;
using RestoreStatus = Corvid.Kernel.Checkpoints.RestoreStatus;
using AppScope = Corvid.App.Core.Sessions.RewindScope;

namespace Corvid.App.Engine.Tests;

/// <summary>Rewind on the engine: the ledger as a timeline, the kernel's dry run as the plan and preview, and each restore scope.</summary>
public sealed class EngineRewindTests
{
    private const string Story = "one\ntwo\nthree\nfour\nfive\n";
    private const string Corvids = "ONE\ntwo\nthree\nfour\nfive\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Turn 1 rewrites story.txt, turn 2 creates notes.md, turn 3 says something with no call.</summary>
    private static async Task<string> ThreeTurnsAsync(HistoryFixture fixture)
    {
        fixture.Write("story.txt", Story);
        var id = fixture.Session();
        await fixture.WriteTurnAsync(id, "call-1", "story.txt", Corvids, "capitalise the story");
        await fixture.WriteTurnAsync(id, "call-2", "notes.md", "a note\n", "make notes");
        fixture.Say(id, "thanks");
        return id;
    }

    private static LedgerEntry[] Calls(RewindTimeline timeline) => [.. timeline.Entries.Where(entry => entry.Kind != LedgerEntryKind.Restore)];

    [Fact]
    public async Task TheTimelineIsTheLedgerNewestFirstWithEachCallsToolTargetTurnAndCounts()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();

        var timeline = await rewind.GetTimelineAsync(id, Ct);

        Assert.Equal(3, timeline.Turn);
        Assert.Null(timeline.LastRestore);
        var ledger = fixture.Runtime.TryGetSession(id, out var session) ? session.Checkpoints() : [];
        Assert.Equal(2, timeline.Entries.Length);
        var notes = timeline.Entries[0];
        Assert.Equal((LedgerEntryKind.Call, "write_file", "notes.md", 2), (notes.Kind, notes.Tool, notes.Target, notes.Turn));
        Assert.Equal(ledger[1].Before.Checkpoint.CheckpointId, notes.CheckpointId);
        Assert.Equal(ledger[1].After!.Checkpoint.CheckpointId, notes.AfterId);
        var created = Assert.Single(notes.Changes);
        Assert.Equal(("notes.md", 1, 0), (created.Path, created.Added, created.Removed));
        var story = timeline.Entries[1];
        Assert.Equal(1, story.Turn);
        Assert.Equal(("story.txt", 1, 1), (story.Changes[0].Path, story.Changes[0].Added, story.Changes[0].Removed));
        Assert.True(story.At > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task APlanListsWhatGoesBackTheTurnsThatLeaveAndTheCommandForIt()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var timeline = await rewind.GetTimelineAsync(id, Ct);
        var first = timeline.Entries[^1];

        var plan = await rewind.PlanAsync(id, first.CheckpointId, Ct);

        Assert.Equal((first.CheckpointId, 1), (plan.CheckpointId, plan.Turn));
        Assert.Equal("/rollback 2", plan.Command);
        // Going back to before turn 1's call keeps turn 1's message; turns 2 and 3 leave.
        Assert.Equal((2, 3, 2), (plan.FromTurn, plan.ToTurn, plan.TurnsLeaving));
        Assert.Equal(["story.txt", "notes.md"], plan.Files.Select(file => file.Path));
        Assert.All(plan.Files, file => Assert.Equal(PutBackKind.Restore, file.Kind));
        Assert.False(plan.HasConflicts);

        var latest = await rewind.PlanAsync(id, timeline.Entries[0].CheckpointId, Ct);
        Assert.Equal("/rollback 1", latest.Command);
        Assert.Equal(["notes.md"], latest.Files.Select(file => file.Path));
    }

    [Fact]
    public async Task APlanMergesWithThePersonsEditAndStopsOnOneItWouldLose()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        // After Corvid: the person edits another part of story.txt, and rewrites the notes Corvid created.
        fixture.Write("story.txt", "ONE\ntwo\nthree\nfour\nfive, by me\n");
        fixture.Write("notes.md", "my own notes\n");
        using var rewind = fixture.Rewind();
        var timeline = await rewind.GetTimelineAsync(id, Ct);

        var plan = await rewind.PlanAsync(id, timeline.Entries[^1].CheckpointId, Ct);

        var story = plan.Files.Single(file => file.Path == "story.txt");
        Assert.Equal(PutBackKind.Merge, story.Kind);
        Assert.NotNull(story.Edit);
        Assert.Equal(timeline.Entries[^1].AfterId, story.Edit.AfterCheckpoint);
        var notes = plan.Files.Single(file => file.Path == "notes.md");
        Assert.Equal(PutBackKind.Conflict, notes.Kind);
        Assert.Contains("created it", notes.Reason, StringComparison.Ordinal);
        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public async Task ThePreviewShowsEachFileNowAndAsTheRestoreWouldLeaveIt()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var timeline = await rewind.GetTimelineAsync(id, Ct);

        var files = await rewind.PreviewAsync(id, timeline.Entries[^1].CheckpointId, Ct);

        var story = files.Single(file => file.Path == "story.txt");
        Assert.Equal((Corvids, Story), (story.CurrentText, story.RestoredText));
        var notes = files.Single(file => file.Path == "notes.md");
        Assert.Equal(("a note\n", ""), (notes.CurrentText, notes.RestoredText));
        // Nothing was touched to make it.
        Assert.Equal(Corvids, fixture.Read("story.txt"));
    }

    [Fact]
    public async Task RestoringFilesAndConversationPutsTheFilesBackAndListsTheRestorePoint()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var raised = new Raised();
        rewind.TimelineChanged += raised.Add;
        var first = (await rewind.GetTimelineAsync(id, Ct)).Entries[^1];

        var outcome = await rewind.RestoreAsync(id, first.CheckpointId, AppScope.FilesAndConversation, Ct);

        Assert.False(outcome.Refused);
        Assert.Equal(["notes.md", "story.txt"], outcome.Restored.Order(StringComparer.Ordinal));
        Assert.NotNull(outcome.CheckpointId);
        Assert.Equal(Story, fixture.Read("story.txt"));
        Assert.False(File.Exists(fixture.PathOf("notes.md")));
        Assert.Equal([id], raised.Ids);

        var after = await rewind.GetTimelineAsync(id, Ct);
        Assert.Empty(Calls(after));
        var point = Assert.Single(after.Entries);
        Assert.Equal((LedgerEntryKind.Restore, outcome.CheckpointId), (point.Kind, point.CheckpointId));
        Assert.Same(outcome, point.Outcome);
        Assert.Same(outcome, after.LastRestore);
        Assert.Equal(1, after.Turn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rewind.PlanAsync(id, point.CheckpointId, Ct));
    }

    [Fact]
    public async Task AConversationOnlyRestoreKeepsTheFiles()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var latest = (await rewind.GetTimelineAsync(id, Ct)).Entries[0];

        var outcome = await rewind.RestoreAsync(id, latest.CheckpointId, AppScope.ConversationOnly, Ct);

        Assert.False(outcome.Refused);
        Assert.Empty(outcome.Restored);
        Assert.Equal("a note\n", fixture.Read("notes.md"));
        var after = await rewind.GetTimelineAsync(id, Ct);
        Assert.Single(Calls(after));
        Assert.Equal(AppScope.ConversationOnly, after.LastRestore!.Scope);
        Assert.Equal(2, after.Turn);
    }

    [Fact]
    public async Task ARestoreReachesTheOpenTranscriptAndTheNextPromptTakesTheTurnThatLeft()
    {
        await using var fixture = await HistoryFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var live = new Recorder();
        using var subscription = await fixture.Sessions.OpenAsync(id, live.Add, Ct);
        var latest = (await rewind.GetTimelineAsync(id, Ct)).Entries[0];
        Assert.Equal(2, latest.Turn);

        var outcome = await rewind.RestoreAsync(id, latest.CheckpointId, AppScope.ConversationOnly, Ct);

        Assert.False(outcome.Refused);
        var rewound = Assert.Single(live.Updates.OfType<Corvid.App.Core.Sessions.SessionRewound>());
        Assert.Equal((2, AppScope.ConversationOnly, latest.CheckpointId), (rewound.Turn, rewound.Scope, rewound.RestorePoint));
        var view = Corvid.App.Core.Sessions.SessionFold.Replay(id, [.. subscription.History, .. live.Updates]);
        Assert.DoesNotContain(view.Transcript, entry => entry.Turn >= 2);

        await fixture.Sessions.SendAsync(id, "hello", Ct);
        await fixture.Sessions.IdleAsync();
        Assert.Equal(2, Assert.Single(live.Updates.OfType<Corvid.App.Core.Sessions.TurnStarted>()).Turn);
    }

    [Fact]
    public async Task AFilesOnlyRestoreKeepsTheConversation()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var first = (await rewind.GetTimelineAsync(id, Ct)).Entries[^1];

        var outcome = await rewind.RestoreAsync(id, first.CheckpointId, AppScope.FilesOnly, Ct);

        Assert.False(outcome.Refused);
        Assert.Equal(Story, fixture.Read("story.txt"));
        var after = await rewind.GetTimelineAsync(id, Ct);
        Assert.Equal(2, Calls(after).Length);
        Assert.Equal(3, after.Turn);
        var point = after.Entries[0];
        Assert.Equal(LedgerEntryKind.Restore, point.Kind);
        Assert.Equal(AppScope.FilesOnly, point.Outcome!.Scope);
    }

    [Fact]
    public async Task ARefusedRestoreChangesNothingAndIsTheLastRestore()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        fixture.Write("notes.md", "my own notes\n");
        using var rewind = fixture.Rewind();
        var first = (await rewind.GetTimelineAsync(id, Ct)).Entries[^1];
        var head = fixture.Runtime.Log.GetHead(id, Ct);

        var outcome = await rewind.RestoreAsync(id, first.CheckpointId, AppScope.FilesAndConversation, Ct);

        Assert.True(outcome.Refused);
        Assert.Equal("notes.md", Assert.Single(outcome.Conflicts).Path);
        Assert.EndsWith("Nothing was overwritten.", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(Corvids, fixture.Read("story.txt"));
        Assert.Equal(head, fixture.Runtime.Log.GetHead(id, Ct));
        Assert.Same(outcome, (await rewind.GetTimelineAsync(id, Ct)).LastRestore);
    }

    [Fact]
    public async Task ARestoreMadeElsewhereIsReadBackFromTheLog()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        fixture.Runtime.TryGetSession(id, out var session);
        Assert.Equal(RestoreStatus.Restored, (await session.RollbackAsync(1, Ct))!.Status);
        using var rewind = fixture.Rewind();

        var timeline = await rewind.GetTimelineAsync(id, Ct);

        var point = timeline.Entries[0];
        Assert.Equal(LedgerEntryKind.Restore, point.Kind);
        Assert.Equal("rollback", point.Target);
        Assert.Equal(["notes.md"], point.Outcome!.Restored);
        Assert.Equal(AppScope.FilesAndConversation, point.Outcome.Scope);
        Assert.Equal(point.Outcome, timeline.LastRestore);
    }

    [Fact]
    public async Task AnUnknownSessionOrCheckpointIsNotFound()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => rewind.GetTimelineAsync("cli-none", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rewind.PlanAsync(id, "cp-none", Ct));
    }

    [Fact]
    public async Task WithoutARuntimeTheTimelineSaysWhy()
    {
        await using var host = TestHost.Create(new FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Ct);
        using var sessions = new EngineSessionService(engine, new InlineDispatcher(), TimeProvider.System);
        using var signals = new HistorySignals(sessions, new InlineDispatcher());
        using var rewind = new EngineRewindService(engine, TimeProvider.System, signals);

        var refused = await Assert.ThrowsAsync<ServiceUnavailableException>(() => rewind.GetTimelineAsync("cli-1", Ct));
        Assert.StartsWith("Corvid cannot start", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATurnThatEndsOrARevertTellsTheTimeline()
    {
        await using var fixture = await HistoryFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        var id = await ThreeTurnsAsync(fixture);
        using var rewind = fixture.Rewind();
        var raised = new Raised();
        rewind.TimelineChanged += raised.Add;

        await fixture.Sessions.SendAsync(id, "hello", Ct);
        await fixture.Sessions.IdleAsync();

        Assert.NotEmpty(raised.Ids);
        Assert.All(raised.Ids, raisedId => Assert.Equal(id, raisedId));
        var count = raised.Ids.Count;
        fixture.Signals.Raise(id);
        Assert.Equal(count + 1, raised.Ids.Count);
        // Disposed with the window: it hears nothing more.
        rewind.Dispose();
        fixture.Signals.Raise(id);
        Assert.Equal(count + 1, raised.Ids.Count);
    }
}
