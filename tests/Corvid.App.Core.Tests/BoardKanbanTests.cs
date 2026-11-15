using System.Text.Json;
using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// C-043: the Board as a kanban (docs/plan/ui.md §4.13): each card's column from its summary, the person's Mark done, Not
/// now and Reopen, a card coming back by itself when something new happens, the status line, read-only sessions, job runs,
/// the rail's "your turn" and the Board's badge, over the demo's scenario and summaries it is told about.
/// </summary>
public sealed class BoardKanbanTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Rig(BoardViewModel Board, ShellViewModel Shell, DemoSessionService Sessions, ManualTimeProvider Time);

    private static Rig Start(DemoScene scene = DemoScene.Approval)
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell);
        var sessions = new DemoSessionService(dispatcher, time, scene);
        var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        hub.StartAsync(null, DemoShell.CurrentSessionId, Ct).GetAwaiter().GetResult();
        shell.Navigate(Area.Board);
        var board = new BoardViewModel(shell, hub, new DemoBoardService(time), new SessionServiceWatcher(sessions), time, dispatcher);
        return new Rig(board, shell, sessions, time);
    }

    private static List<string> In(BoardViewModel board, BoardColumn column) => [.. board.Column(column).Cards.Select(card => card.Id)];

    private static SessionSummary Idle(string id = "s") => new(id, "a session") { State = SessionState.Idle, Workspace = "corvid" };

    // ---- The column, as a pure function of the summary ----

    public static TheoryData<string, SessionSummary, BoardColumn> Columns => new()
    {
        { "idle, nothing asked", Idle(), BoardColumn.Parked },
        { "idle, finished", Idle() with { Ending = TurnEnding.Finished }, BoardColumn.Parked },
        { "your turn", Idle() with { Ending = TurnEnding.AsksYou }, BoardColumn.NeedsYou },
        { "your turn put aside", Idle() with { Ending = TurnEnding.AsksYou, Mark = BoardMark.Parked }, BoardColumn.Parked },
        { "your turn marked done", Idle() with { Ending = TurnEnding.AsksYou, Mark = BoardMark.Done }, BoardColumn.Done },
        { "marked done", Idle() with { Mark = BoardMark.Done }, BoardColumn.Done },
        { "running", Idle() with { State = SessionState.Running }, BoardColumn.Running },
        { "running, marked done before", Idle() with { State = SessionState.Running, Mark = BoardMark.Done }, BoardColumn.Running },
        { "an approval inside a running turn", Idle() with { State = SessionState.Waiting, PendingApprovals = 1 }, BoardColumn.NeedsYou },
        { "an approval while running", Idle() with { State = SessionState.Running, PendingApprovals = 2 }, BoardColumn.NeedsYou },
        { "a question", Idle() with { State = SessionState.Waiting, PendingQuestions = 1 }, BoardColumn.NeedsYou },
        { "a question queued in a running turn", Idle() with { State = SessionState.Running, PendingQuestions = 1 }, BoardColumn.Running },
        { "a question, parked", Idle() with { State = SessionState.Waiting, PendingQuestions = 1, Mark = BoardMark.Parked }, BoardColumn.NeedsYou },
        { "working in a terminal", Idle() with { ReadOnly = true, WorkingElsewhere = true }, BoardColumn.Running },
        { "held by a terminal, idle", Idle() with { ReadOnly = true, Ending = TurnEnding.AsksYou }, BoardColumn.NeedsYou },
        { "a job's run going", Idle() with { State = SessionState.Job, Job = "nightly", JobRunning = true }, BoardColumn.Running },
        { "a job's run ended", Idle() with { State = SessionState.Job, Job = "nightly" }, BoardColumn.Done },
        { "a job's run failed", Idle() with { State = SessionState.Job, Job = "nightly", JobFailed = true }, BoardColumn.Done },
    };

    [Theory]
    [MemberData(nameof(Columns))]
    public void EachCardsColumnFollowsThePrecedence(string what, SessionSummary summary, BoardColumn expected)
    {
        Assert.True(expected == BoardText.Column(summary), what);
    }

    [Fact]
    public void TheStatusLinePutsTheRunningCallAndWhatWaitsBeforeTheEnginesLine()
    {
        var idle = Idle() with { StatusLine = "PR #12 is up; waits on your merge", StatusGenerated = true, Preview = "PR #12 is up." };

        Assert.Equal("PR #12 is up; waits on your merge", BoardText.StatusLine(idle));
        Assert.Equal("write_file docs/notes.md · step 2 of 6",
            BoardText.StatusLine(idle with { State = SessionState.Running, Activity = "write_file docs/notes.md", Step = 2, MaxSteps = 6 }));
        Assert.Equal("approval · rm -rf build/", BoardText.StatusLine(idle with { State = SessionState.Waiting, PendingApprovals = 1, Approval = "rm -rf build/" }));
        Assert.Equal("question · which version?", BoardText.StatusLine(idle with { State = SessionState.Waiting, PendingQuestions = 1, Question = "which version?" }));
        Assert.Equal("working in another process", BoardText.StatusLine(idle with { ReadOnly = true, WorkingElsewhere = true }));
        // Without the engine's line, the latest exchange's; a hidden character in model text is shown, never kept invisible.
        Assert.Equal("PR #12 is up.", BoardText.StatusLine(idle with { StatusLine = null }));
        Assert.Equal("two lines", BoardText.StatusLine(Idle() with { StatusLine = "two\nlines" }));
    }

    // ---- The demo's Board ----

    [Fact]
    public void TheYourTurnCardAsksAndOffersNotNowAndTheRailSaysSo()
    {
        var rig = Start();
        var card = rig.Board.Cards[DemoSessionService.BumpSessionId];

        Assert.Equal(BoardColumn.NeedsYou, card.Column);
        Assert.Equal("your turn", card.StateLabel);
        Assert.Equal(Tone.Amber, card.StateTone);
        Assert.True(card.IsWaiting);
        Assert.True(card.ShowsStatusLine);
        Assert.Equal("Starling bumped to 0.3.0, tests pass; PR #131 waits on your merge", card.StatusLine);
        Assert.True(card.ShowsNotNow);
        Assert.True(card.CanMarkDone);
        Assert.Equal("your turn", rig.Shell.FindSession(DemoSessionService.BumpSessionId)!.StateLabel);
        // The Board's badge counts Needs you; the bell stays with approvals and questions (and the areas' items).
        Assert.Equal(2, rig.Shell.RailItem(Area.Board).Badge);
        Assert.Equal(6, rig.Shell.AttentionCount);
    }

    [Fact]
    public async Task NotNowParksAYourTurnUntilSomethingNewHappensInIt()
    {
        var rig = Start();
        var card = rig.Board.Cards[DemoSessionService.BumpSessionId];

        card.NotNowCommand.Execute(null);

        Assert.Equal(BoardColumn.Parked, card.Column);
        Assert.Equal([DemoSessionService.BumpSessionId, "cli-77b0"], In(rig.Board, BoardColumn.Parked));
        Assert.False(card.ShowsNotNow);
        Assert.Equal("idle", rig.Shell.FindSession(DemoSessionService.BumpSessionId)!.StateLabel);
        Assert.Equal(1, rig.Shell.RailItem(Area.Board).Badge);

        // A new message is news: the card leaves Parked with no action, and runs.
        await rig.Sessions.SendAsync(DemoSessionService.BumpSessionId, "merge it", Ct);

        Assert.Equal(BoardColumn.Running, card.Column);
    }

    [Fact]
    public async Task MarkDoneHoldsUntilNewsAndReopenTakesItOutAtOnce()
    {
        var rig = Start(DemoScene.Main);
        var card = rig.Board.Cards[DemoSessionService.VaultSessionId];
        Assert.True(card.SuggestsDone);
        Assert.False(card.ShowsMarkDone);

        card.MarkDoneCommand.Execute(null);

        Assert.Equal(BoardColumn.Done, card.Column);
        Assert.Equal("done", card.StateLabel);
        Assert.True(card.ShowsReopen);
        Assert.False(card.SuggestsDone);
        Assert.Equal([DemoSessionService.VaultSessionId, DemoSessionService.JobSessionId], In(rig.Board, BoardColumn.Done));

        card.ReopenCommand.Execute(null);
        Assert.Equal(BoardColumn.Parked, card.Column);

        card.MarkDoneCommand.Execute(null);
        Assert.Equal(BoardColumn.Done, card.Column);
        // A title is not news; a message is.
        await rig.Sessions.RenameAsync(DemoSessionService.VaultSessionId, "Vault policy for ntfy, send-only", Ct);
        Assert.Equal(BoardColumn.Done, card.Column);
        await rig.Sessions.SendAsync(DemoSessionService.VaultSessionId, "also bind it to POST only", Ct);
        Assert.Equal(BoardColumn.Running, card.Column);
        Assert.False(card.ShowsReopen);
    }

    [Fact]
    public async Task MarkDoneIsDisabledWithWhyWhileSomethingIsOpenOrRunning()
    {
        var rig = Start();
        var waiting = rig.Board.Cards[DemoSessionService.MainSessionId];
        var asking = rig.Board.Cards[DemoSessionService.ReleaseNotesSessionId];
        var running = rig.Board.Cards[DemoSessionService.TerminalSessionId];

        Assert.True(waiting.ShowsMarkDone);
        Assert.False(waiting.MarkDoneCommand.CanExecute(null));
        Assert.Equal("Answer it first: an approval or question is open", waiting.MarkDoneHelp);
        Assert.False(asking.CanMarkDone);
        Assert.Equal("Answer it first: an approval or question is open", asking.MarkDoneHelp);
        Assert.False(running.CanMarkDone);
        Assert.Equal("A turn is running: mark it done once it ends", running.MarkDoneHelp);
        // The demo's service refuses it too, as the engine does.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sessions.SetBoardMarkAsync(DemoSessionService.ReleaseNotesSessionId, BoardMark.Done, Ct));
        // A job's run is never marked: it is Done once it ended.
        Assert.False(rig.Board.Cards[DemoSessionService.JobSessionId].ShowsMarkDone);
    }

    [Fact]
    public void WithoutUiDbTheMovesAreDisabledAndSayWhy()
    {
        var card = new SessionCardViewModel(Idle() with { Ending = TurnEnding.AsksYou }, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, _ => { }, _ => { }, () => { });

        Assert.True(card.ShowsMarkDone);
        Assert.False(card.MarkDoneCommand.CanExecute(null));
        Assert.False(card.NotNowCommand.CanExecute(null));
        Assert.Equal("The Board's Done is kept in ui.db, which the engine on this machine keeps", card.MarkDoneHelp);
    }

    [Fact]
    public void ANewSessionWithNothingSentCannotBeMarkedDone()
    {
        var marker = (Func<SessionCardViewModel, BoardMark?, Task>)((_, _) => Task.CompletedTask);
        var fresh = new SessionCardViewModel(Idle() with { Title = "New session" }, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, _ => { }, _ => { }, () => { }) { Marker = marker };
        var sent = new SessionCardViewModel(Idle() with { Turns = 1 }, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, _ => { }, _ => { }, () => { }) { Marker = marker };

        Assert.False(fresh.MarkDoneCommand.CanExecute(null));
        Assert.Equal("Nothing has been sent in it yet", fresh.MarkDoneHelp);
        Assert.True(sent.MarkDoneCommand.CanExecute(null));
    }

    [Fact]
    public void TheRailSaysYourTurnOnlyWhileTheBoardWould()
    {
        var rig = Start();
        var asks = Idle("cli-asks") with { Ending = TurnEnding.AsksYou, Turns = 1, LastActivity = rig.Time.GetUtcNow() };

        rig.Sessions.Announce(asks);
        Assert.Equal("your turn", rig.Shell.FindSession("cli-asks")!.StateLabel);
        // Parked with Not now, or marked done: the rail says idle, as the card leaves Needs you.
        rig.Sessions.Announce(asks with { Mark = BoardMark.Parked });
        Assert.Equal("idle", rig.Shell.FindSession("cli-asks")!.StateLabel);
        rig.Sessions.Announce(asks with { Mark = BoardMark.Done });
        Assert.Equal("idle", rig.Shell.FindSession("cli-asks")!.StateLabel);
        // A question is the formal wait, not a rule's your turn.
        rig.Sessions.Announce(asks with { State = SessionState.Waiting, PendingQuestions = 1 });
        Assert.Equal("waiting on you", rig.Shell.FindSession("cli-asks")!.StateLabel);
    }

    [Fact]
    public void ASessionATerminalDrivesRunsWhileItWorksAndCanBeMarkedOnceIdle()
    {
        var rig = Start();
        var held = Idle("cli-term") with { ReadOnly = true, Holder = "open in a terminal (corvid chat --session cli-term)", LastActivity = rig.Time.GetUtcNow(), Turns = 3 };

        rig.Sessions.Announce(held with { WorkingElsewhere = true, Activity = "shell dotnet test" });
        var card = rig.Board.Cards["cli-term"];
        Assert.Equal(BoardColumn.Running, card.Column);
        Assert.Equal("running elsewhere", card.StateLabel);
        Assert.Contains(card.Tags, tag => tag.Text == "read-only");
        Assert.False(card.CanMarkDone);

        rig.Sessions.Announce(held with { Ending = TurnEnding.Finished });
        Assert.Equal(BoardColumn.Parked, card.Column);
        Assert.True(card.SuggestsDone);
        Assert.True(card.CanMarkDone);
        Assert.Contains(card.Tags, tag => tag.Text == "read-only");
    }

    [Fact]
    public void AJobRunRunsUntilItEndsAndAFailedOneSaysSo()
    {
        var rig = Start();
        var run = Idle("job:nightly:r2") with { State = SessionState.Job, Job = "nightly", RunAt = rig.Time.GetUtcNow(), ReadOnly = true };

        rig.Sessions.Announce(run with { JobRunning = true });
        var card = rig.Board.Cards["job:nightly:r2"];
        Assert.Equal(BoardColumn.Running, card.Column);
        Assert.Equal("running", card.StateLabel);

        rig.Sessions.Announce(run with { JobFailed = true });
        Assert.Equal(BoardColumn.Done, card.Column);
        Assert.Equal("failed", card.StateLabel);
        Assert.Equal(["job", "failed"], card.Tags.Select(tag => tag.Text));
        Assert.Equal(Tone.Red, card.Tags[1].Tone);
    }

    [Fact]
    public void DoneShowsTheNewestTwentyThenShowMore()
    {
        var rig = Start();
        var start = rig.Time.GetUtcNow();
        for (var i = 0; i < 25; i++)
            rig.Sessions.Announce(Idle($"old-{i:00}") with { Mark = BoardMark.Done, MarkedAt = start.AddMinutes(-i), LastActivity = start.AddHours(-1) });

        var done = rig.Board.Column(BoardColumn.Done);
        Assert.Equal(26, done.Count);
        Assert.Equal(BoardColumnViewModel.DoneCap, done.Cards.Count);
        Assert.Equal("old-00", done.Cards[0].Id);
        Assert.Equal("Show 6 more", done.MoreText);
        Assert.Equal("Done · 26", done.Head);

        done.ShowMoreCommand.Execute(null);

        Assert.Equal(26, done.Cards.Count);
        Assert.False(done.HasMore);
    }

    [Fact]
    public void AStackedColumnFolds()
    {
        var column = Start().Board.Column(BoardColumn.Parked);

        column.ToggleCommand.Execute(null);

        Assert.True(column.IsCollapsed);
        Assert.Equal("Show Parked", column.ToggleLabel);
    }

    [Fact]
    public void TheNewSummaryFieldsCrossTheWireAndAnOlderEnginesSummaryReadsAsBefore()
    {
        var summary = Idle() with
        {
            Ending = TurnEnding.AsksYou,
            Mark = BoardMark.Parked,
            MarkedAt = DateTimeOffset.UnixEpoch,
            StatusLine = "waits on your merge",
            StatusGenerated = true,
            WorkingElsewhere = true,
            JobRunning = true,
            JobFailed = true,
        };

        var json = JsonSerializer.Serialize(summary, AttachedJson.Summary);
        Assert.Contains("\"ending\":\"AsksYou\"", json, StringComparison.Ordinal);
        Assert.Equal(summary, JsonSerializer.Deserialize(json, AttachedJson.Summary));

        // G-1: an attached engine from before C-043 sends none of them.
        var older = JsonSerializer.Deserialize("""{"id":"s","title":"a session","state":"Idle"}""", AttachedJson.Summary)!;
        Assert.Equal((TurnEnding.None, (BoardMark?)null, (string?)null, false), (older.Ending, older.Mark, older.StatusLine, older.WorkingElsewhere));
        Assert.Equal(BoardColumn.Parked, BoardText.Column(older));
    }
}
