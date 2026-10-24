using Corvid.App.Core;
using Corvid.App.Core.Board;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class BoardViewModelTests
{
    private sealed record Rig(BoardViewModel Board, ShellViewModel Shell, StatusBarViewModel Bar, DemoSessionService Sessions,
        DemoBoardService Service, ManualTimeProvider Time);

    private static Rig Start(DemoScene scene = DemoScene.Approval, Area area = Area.Board)
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell);
        var sessions = new DemoSessionService(dispatcher, time, scene);
        var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        hub.StartAsync(null, DemoShell.CurrentSessionId, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        shell.Navigate(area);
        var service = new DemoBoardService(time);
        var board = new BoardViewModel(shell, hub, service, new SessionServiceWatcher(sessions), time, dispatcher);
        return new Rig(board, shell, bar, sessions, service, time);
    }

    private static string Text(IEnumerable<MarkdownRun> runs) => string.Concat(runs.Select(run => run.Text));

    private static SessionCardViewModel Card(Rig rig, string id) => rig.Board.Cards[id];

    [Fact]
    public void TheBoardDrawsEverySessionAsTheMockupsCardsInItsOrder()
    {
        var rig = Start();

        var cards = rig.Board.Tiles.OfType<SessionCardViewModel>().Select(card => card.Id).ToList();
        Assert.Equal(["cli-9f2e", "cli-a410", "cli-77b0", "cli-c3d2", DemoSessionService.JobSessionId], cards);
        Assert.IsType<NewSessionTile>(rig.Board.Tiles[^1]);
        Assert.Equal("5 sessions · 2 workspaces · 1 job run", rig.Board.Counts);
    }

    [Fact]
    public void TheWaitingCardSaysWhatItWaitsOnAndHowLongIsLeft()
    {
        var card = Card(Start(), "cli-9f2e");

        Assert.Equal("Fix the flaky sandbox test on CI", card.Title);
        Assert.Equal("corvid · main · cli-9f2e · fork of cli-8c11", Text(card.Meta));
        Assert.Equal(Tone.Amber, card.StateTone);
        Assert.Equal("waiting on you", card.StateLabel);
        Assert.Equal("approval 1 of 2 · 4:12 left", Text(card.StateDetail));
        Assert.Equal("step 2 of 6 · claude (escalated) · 13 turns", card.Detail);
        Assert.Equal(["tainted", "cloud"], card.Tags.Select(tag => tag.Text));
        Assert.True(card.IsWaiting);
        Assert.True(card.CanOpen);
        Assert.True(card.CanPeek);
    }

    [Fact]
    public void TheRunningCardNamesItsCallWithTheTargetInMonospaceAndTheQueuedQuestion()
    {
        var card = Card(Start(), "cli-a410");

        Assert.Equal("running", card.StateLabel);
        Assert.Equal(Tone.Blue, card.StateTone);
        Assert.Equal([new MarkdownRun("write_file "), new MarkdownRun("docs/releases/v0.2.md", InlineStyle.Code)], card.StateDetail);
        Assert.Equal("step 3 of 5 · qwen3.5-0.8b · 2 m · a question is queued for you", card.Detail);
        Assert.Equal(["local"], card.Tags.Select(tag => tag.Text));
    }

    [Fact]
    public void TheIdleCardTellsTheLastActivityAndWhatTheLastTurnStaged()
    {
        var card = Card(Start(), "cli-77b0");

        Assert.Equal("corvid-vault · ntfy · cli-77b0", Text(card.Meta));
        Assert.Equal("idle", card.StateLabel);
        Assert.Equal(Tone.Grey, card.StateTone);
        Assert.Equal("last activity 1 h ago", Text(card.StateDetail));
        Assert.Equal("6 turns · memory staged: 1 fact", card.Detail);
        Assert.False(card.CanPeek);
    }

    [Fact]
    public void ASessionAnotherProcessDrivesIsAReadOnlyLiveViewWithItsCommandInMonospace()
    {
        var card = Card(Start(), "cli-c3d2");

        Assert.Equal(
            [new MarkdownRun("corvid · main · cli-c3d2 · open in a terminal ("), new MarkdownRun("corvid chat --session cli-c3d2", InlineStyle.Code), new MarkdownRun(")")],
            card.Meta);
        Assert.Equal("held by another process", card.StateLabel);
        Assert.Equal("read-only live view", Text(card.StateDetail));
        Assert.Equal("step 1 of 4 · qwen3.5-0.8b · 40 s", card.Detail);
        Assert.Equal(["read-only"], card.Tags.Select(tag => tag.Text));
        Assert.False(card.CanOpen);
        Assert.True(card.CanPeek);
        Assert.False(card.TakeOverCommand.CanExecute(null));
    }

    [Fact]
    public void AJobRunSaysWhenItFinishedWhatItCalledAndWhatItLeft()
    {
        var card = Card(Start(), DemoSessionService.JobSessionId);

        Assert.Equal("nightly-tidy · run of 09:00", card.Title);
        Assert.Equal("job:nightly-tidy:2026-10-24 · every day at 9am", Text(card.Meta));
        Assert.Equal(Tone.Violet, card.StateTone);
        Assert.Equal("finished", card.StateLabel);
        Assert.Equal("09:00:41 · 3 tool calls", Text(card.StateDetail));
        Assert.Equal("staged 2 memory facts for review · delivered to the Telegram home chat", card.Detail);
        Assert.Equal(["job"], card.Tags.Select(tag => tag.Text));
        Assert.True(card.OpenRunCommand.CanExecute(null));
        Assert.False(card.CanOpen);
    }

    [Fact]
    public void TheTrayListsTheSessionsItemsThenTheAreasAndTheBellAgrees()
    {
        var rig = Start();

        Assert.Equal("Attention · 6", rig.Board.AttentionHead);
        Assert.Equal(
            [AttentionKind.Approval, AttentionKind.Question, AttentionKind.McpTool, AttentionKind.Pairing, AttentionKind.Memory, AttentionKind.Job],
            rig.Board.Attention.Select(item => item.Kind));
        var approval = rig.Board.Attention[0];
        Assert.Equal("Approval · rm -rf build/ && dotnet test Corvid.slnx", approval.What);
        Assert.Equal("Fix the flaky sandbox test · 4:12 left · opens the full card", approval.Detail);
        Assert.Equal(["Open card"], approval.Buttons.Select(button => button.Label));
        var question = rig.Board.Attention[1];
        Assert.Equal("Question · which version number?", question.What);
        Assert.Equal("Draft release notes · \"0.2.0, or 0.2.0-rc1 for the first tag?\"", question.Detail);
        Assert.Equal(["0.2.0", "0.2.0-rc1", "Answer…"], question.Buttons.Select(button => button.Label));
        Assert.Equal(6, rig.Shell.AttentionCount);
    }

    [Theory]
    [InlineData("Fix the flaky sandbox test on CI", "Fix the flaky sandbox test")]
    [InlineData("Draft release notes for v0.2", "Draft release notes")]
    [InlineData("Vault policy for ntfy", "Vault policy for ntfy")]
    [InlineData("Spike S4 write-up", "Spike S4 write-up")]
    public void TheTrayNamesASessionWithoutItsClosingQualifier(string title, string expected) =>
        Assert.Equal(expected, BoardText.ShortTitle(title));

    [Fact]
    public void InTheMainSceneTheTrayHasNoApproval()
    {
        var rig = Start(DemoScene.Main);

        Assert.Equal("Attention · 5", rig.Board.AttentionHead);
        Assert.DoesNotContain(rig.Board.Attention, item => item.Kind == AttentionKind.Approval);
        Assert.Equal(5, rig.Shell.AttentionCount);
    }

    [Fact]
    public void AnsweringTheQuestionFromTheTrayResumesTheSessionAndTheItemGoes()
    {
        var rig = Start();
        var question = rig.Board.Attention.Single(item => item.Kind == AttentionKind.Question);

        question.Buttons[0].Command.Execute(null);

        Assert.DoesNotContain(rig.Board.Attention, item => item.Kind == AttentionKind.Question);
        Assert.Equal("Attention · 5", rig.Board.AttentionHead);
        Assert.Equal(5, rig.Shell.AttentionCount);
        Assert.Equal("step 3 of 5 · qwen3.5-0.8b · 2 m", Card(rig, DemoSessionService.ReleaseNotesSessionId).Detail);
    }

    [Fact]
    public void ResolvingAnAreasItemRemovesItAndAnOpenButtonLeavesItListed()
    {
        var rig = Start();
        var pairing = rig.Board.Attention.Single(item => item.Kind == AttentionKind.Pairing);

        pairing.Buttons.Single(button => button.Label == "Pair as guest").Command.Execute(null);

        Assert.Equal([(DemoBoardService.PairingItemId, "guest")], rig.Service.Resolved);
        Assert.DoesNotContain(rig.Board.Attention, item => item.Kind == AttentionKind.Pairing);
        Assert.Equal(5, rig.Shell.AttentionCount);

        rig.Board.Attention.Single(item => item.Kind == AttentionKind.Memory).Buttons[0].Command.Execute(null);
        Assert.Equal(Area.Memory, rig.Shell.CurrentArea);
        Assert.Contains(rig.Board.Attention, item => item.Kind == AttentionKind.Memory);
    }

    [Fact]
    public void OpeningACardAttachesItsSessionAndAJobRunOpensTheRun()
    {
        var rig = Start();

        Card(rig, "cli-a410").OpenCommand.Execute(null);
        Assert.Equal(Area.Sessions, rig.Shell.CurrentArea);
        Assert.Equal("cli-a410", rig.Shell.CurrentSession?.Id);

        rig.Shell.Navigate(Area.Board);
        Card(rig, DemoSessionService.JobSessionId).OpenRunCommand.Execute(null);
        Assert.Equal(DemoSessionService.JobSessionId, rig.Shell.CurrentSession?.Id);

        rig.Shell.Navigate(Area.Board);
        Card(rig, DemoSessionService.JobSessionId).OpenJobsCommand.Execute(null);
        Assert.Equal(Area.Jobs, rig.Shell.CurrentArea);
    }

    [Fact]
    public void TheApprovalItemOpensTheSessionAtItsCard()
    {
        var rig = Start();
        rig.Shell.SelectSession(rig.Shell.FindSession("cli-77b0"));

        rig.Board.Attention[0].Buttons[0].Command.Execute(null);

        Assert.Equal(Area.Sessions, rig.Shell.CurrentArea);
        Assert.Equal("cli-9f2e", rig.Shell.CurrentSession?.Id);
    }

    [Fact]
    public void PeekShowsTheLatestExchangeReadOnlyAndFollowsTheSession()
    {
        var rig = Start();
        var card = Card(rig, "cli-a410");

        card.PeekCommand.Execute(null);

        var peek = Assert.IsType<BoardPeekViewModel>(rig.Board.Peek);
        Assert.Same(card, peek.Card);
        Assert.Equal(Area.Board, rig.Shell.CurrentArea);
        Assert.Contains(peek.Entries, line => line.Label == "you" && line.Text.StartsWith("draft release notes", StringComparison.Ordinal));
        Assert.Contains(peek.Entries, line => line.Label == "write_file" && line.Text == "docs/releases/v0.2.md · running");
        Assert.Equal("question", peek.Entries[^1].Label);

        rig.Board.Attention.Single(item => item.Kind == AttentionKind.Question).Buttons[1].Command.Execute(null);
        Assert.Equal("0.2.0, or 0.2.0-rc1 for the first tag? → 0.2.0-rc1", peek.Entries.Last(line => line.Label == "question").Text);

        rig.Board.ClosePeekCommand.Execute(null);
        Assert.Null(rig.Board.Peek);
        Assert.False(rig.Board.ClosePeekCommand.CanExecute(null));
    }

    [Fact]
    public void TheFilterKeepsOnlyWhatItNamesAndSaysSoWhenNothingIs()
    {
        var rig = Start();

        rig.Board.Filters[1].Choose.Execute(null);
        Assert.Equal(BoardFilter.Running, rig.Board.Filter);
        Assert.Equal(["cli-a410", "cli-c3d2"], rig.Board.Tiles.OfType<SessionCardViewModel>().Select(card => card.Id));
        Assert.DoesNotContain(rig.Board.Tiles, tile => tile is NewSessionTile);
        Assert.True(rig.Board.Filters[1].IsSelected);
        Assert.False(rig.Board.Filters[0].IsSelected);

        rig.Board.Filters[2].Choose.Execute(null);
        Assert.Equal(["cli-9f2e"], rig.Board.Tiles.OfType<SessionCardViewModel>().Select(card => card.Id));

        rig.Board.Filters[3].Choose.Execute(null);
        Assert.Equal([DemoSessionService.JobSessionId], rig.Board.Tiles.OfType<SessionCardViewModel>().Select(card => card.Id));
        Assert.False(rig.Board.IsEmpty);

        rig.Board.Filters[0].Choose.Execute(null);
        Assert.Equal(6, rig.Board.Tiles.Count);
    }

    [Fact]
    public void TheStatusBarIsTheBoardsOnlyWhileTheBoardShows()
    {
        var rig = Start();

        Assert.Equal("in-process · 2 workspaces", rig.Bar.Engine);
        Assert.Equal("", rig.Bar.Generator);
        Assert.Equal("", rig.Bar.Context);
        Assert.Empty(rig.Bar.Tags);
        Assert.Equal("Kev-0.8B, calibrated", rig.Bar.Decider);
        Assert.Equal("here · next: nightly-tidy, tomorrow 09:00", rig.Bar.Scheduler);
        Assert.Equal("telegram up · discord up · slack off", rig.Bar.Gateway);
        Assert.Equal("2 running · 1 waiting", rig.Bar.Trailing);
        Assert.Equal(ApprovalMode.Smart, rig.Bar.Mode);

        rig.Shell.Navigate(Area.Sessions);
        Assert.Equal("", rig.Bar.Engine);
        Assert.Equal("", rig.Bar.Gateway);
        Assert.NotEqual("", rig.Bar.Generator);
    }

    [Fact]
    public void TheBoardLeavesAnotherAreasStatusBarAlone()
    {
        var rig = Start(area: Area.Memory);
        var before = (rig.Bar.Engine, rig.Bar.Gateway, rig.Bar.Decider, rig.Bar.Scheduler, rig.Bar.Trailing, rig.Bar.Generator, rig.Bar.Branch);

        Assert.Equal("", rig.Bar.Engine);
        Assert.Equal("", rig.Bar.Gateway);
        Assert.NotEqual("2 running · 1 waiting", rig.Bar.Trailing);

        // Through the Board to an area that writes no status of its own: what the Board wrote goes again.
        rig.Shell.Navigate(Area.Board);
        Assert.Equal("in-process · 2 workspaces", rig.Bar.Engine);
        rig.Shell.Navigate(Area.Settings);
        Assert.Equal(before, (rig.Bar.Engine, rig.Bar.Gateway, rig.Bar.Decider, rig.Bar.Scheduler, rig.Bar.Trailing, rig.Bar.Generator, rig.Bar.Branch));
    }

    [Fact]
    public void ASessionsWordsAreDrawnWithoutTheirHiddenCharacters()
    {
        var summary = new SessionSummary("cli-1", "Fix the \u202Etset kaep\u202C build")
        {
            State = SessionState.Waiting,
            Question = "which \u200Bversion?",
            Workspace = "corvid\u2066",
        };

        Assert.Equal("Fix the tset kaep build", BoardText.Title(summary, TimeZoneInfo.Utc));
        Assert.Equal("Fix the tset kaep build", BoardText.ShortTitle(summary.Title));
        Assert.Equal("question · which version?", Text(BoardText.StateDetail(summary, null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc)));
        Assert.StartsWith("corvid · cli-1", Text(BoardText.Meta(summary)), StringComparison.Ordinal);
        var running = summary with { State = SessionState.Running, Activity = "shell rm\u202E -rf", Model = "qwen\u202E" };
        Assert.Equal("shell rm -rf", Text(BoardText.StateDetail(running, null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc)));
        Assert.Equal("qwen", BoardText.Detail(running, null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void AQuestionsItemCarriesItsQuestionsId()
    {
        var rig = Start();
        var view = Card(rig, DemoSessionService.ReleaseNotesSessionId).View;
        var question = rig.Board.Attention.Single(item => item.Kind == AttentionKind.Question);

        Assert.Equal($"question:{DemoSessionService.ReleaseNotesSessionId}:{view?.Question?.QuestionId}", question.Id);
    }

    [Fact]
    public void AnItemsReadAloudNameFollowsItsText()
    {
        var item = new AttentionItemViewModel("x", AttentionKind.Memory, "Memory", "2 facts", []);
        var raised = new List<string?>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        item.What = "Memory · 3 facts";
        item.Detail = "staged";

        Assert.Equal(2, raised.Count(name => name == nameof(AttentionItemViewModel.AutomationName)));
        Assert.Equal("Memory · 3 facts. staged", item.AutomationName);
    }

    [Fact]
    public void PeekLinesWithTheSameStyledTextAreEqual()
    {
        Assert.Equal(PeekLine.Answer("corvid", "run `dotnet test` **now**"), PeekLine.Answer("corvid", "run `dotnet test` **now**"));
        Assert.NotEqual(PeekLine.Answer("corvid", "run `dotnet test`"), PeekLine.Answer("corvid", "run **dotnet test**"));
    }

    [Fact]
    public void AcceptingTheHeldMcpToolClearsMcpsBadge()
    {
        var rig = Start();
        Assert.Equal(1, rig.Shell.RailItem(Area.Mcp).Badge);

        rig.Board.Attention.Single(item => item.Kind == AttentionKind.McpTool).Buttons.Single(button => button.Label == "Accept").Command.Execute(null);

        Assert.DoesNotContain(rig.Board.Attention, item => item.Kind == AttentionKind.McpTool);
        Assert.Equal(0, rig.Shell.RailItem(Area.Mcp).Badge);
    }

    [Fact]
    public void NotifyingWhenUnfocusedWaitsForTheEngineAndSaysSo()
    {
        var rig = Start();

        Assert.False(rig.Board.ToggleNotifyCommand.CanExecute(null));
        Assert.Contains("engine", rig.Board.NotifyUnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClockStandsStillWhileTheBoardIsHiddenAndCatchesUpOnReturn()
    {
        var rig = Start(DemoScene.Main);
        var card = Card(rig, "cli-c3d2");
        rig.Shell.Navigate(Area.Skills);

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("step 1 of 4 · qwen3.5-0.8b · 40 s", card.Detail);

        rig.Shell.Navigate(Area.Board);
        Assert.Equal("step 1 of 4 · qwen3.5-0.8b · 1 m", card.Detail);
    }

    [Fact]
    public async Task ANewTurnMovesTheIdleCardUpAmongTheRunningOnes()
    {
        var rig = Start(DemoScene.Main);
        var card = Card(rig, "cli-77b0");

        await rig.Sessions.SendAsync("cli-77b0", "also bind it to POST only", TestContext.Current.CancellationToken);

        Assert.Equal("running", card.StateLabel);
        Assert.Equal(Tone.Blue, card.StateTone);
        Assert.True(card.CanPeek);
        var order = rig.Board.Tiles.OfType<SessionCardViewModel>().Select(tile => tile.Id).ToList();
        Assert.True(order.IndexOf("cli-77b0") < order.IndexOf("cli-c3d2"));
    }

    [Fact]
    public void TheTimeOnTheStepFollowsTheClock()
    {
        var rig = Start(DemoScene.Main);
        var card = Card(rig, "cli-c3d2");
        Assert.Equal("step 1 of 4 · qwen3.5-0.8b · 40 s", card.Detail);

        rig.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("step 1 of 4 · qwen3.5-0.8b · 1 m", card.Detail);
    }

    [Fact]
    public void TheDashedCardNamesTheWorkspaceAndItsButtonsWaitForTheEngine()
    {
        var rig = Start();

        var tile = Assert.IsType<NewSessionTile>(rig.Board.Tiles[^1]);
        Assert.Equal(new MarkdownRun("corvid · main", InlineStyle.Bold), tile.Text[1]);
        Assert.False(tile.NewSessionCommand.CanExecute(null));
        Assert.False(tile.OpenWorkspaceCommand.CanExecute(null));
    }
}
