using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>A5 (docs/plan/ui.md §4.13, F18): child sessions in the views: the fold, the header, the Board's tree and the card.</summary>
public sealed class ChildSessionViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChildSessionChanged Child(string id, string status = ChildSessionChanged.Running) =>
        new(id, "summarise the ‮docs", status)
        {
            Tools = ["read_file", "list​_files"],
            Budget = new ChildBudgetInfo(8, 64_000, 600),
            Mix = "generator qwen3.5-9b",
        };

    [Fact]
    public void A_childs_end_replaces_its_start_and_its_text_is_cleaned()
    {
        var state = SessionFold.Start("cli-1");

        state = SessionFold.Apply(state, Child("child-1"));
        state = SessionFold.Apply(state, Child("child-2"));
        state = SessionFold.Apply(state, Child("child-1", "answered") with { Answer = "done‮", Steps = 2 });

        Assert.Equal(["child-1", "child-2"], state.Children.Select(child => child.ChildId));
        Assert.Equal(("answered", 2), (state.Children[0].Status, state.Children[0].Steps));
        Assert.DoesNotContain('‮', state.Children[0].Goal);
        Assert.DoesNotContain('‮', state.Children[0].Answer!);
        Assert.DoesNotContain(state.Children[0].Tools, tool => tool.Contains('​', StringComparison.Ordinal));
        // No transcript row: the delegate_task call is the transcript's.
        Assert.Empty(state.Transcript);

        var child = SessionFold.Apply(SessionFold.Start("child-1"), new ChildOfSession("cli-1", "summarise"));
        Assert.Equal(("cli-1", "summarise"), (child.ParentId, child.ChildGoal));
    }

    [Fact]
    public async Task The_header_lists_the_children_and_a_child_links_its_parent()
    {
        var time = ManualTimeProvider.Demo();
        var service = new DemoSessionService(new InlineDispatcher(), time, DemoScene.Main);
        using var session = new SessionViewModel(DemoSessionService.MainSessionId, service, new InlineDispatcher(), time);
        await session.OpenAsync(Ct);
        Assert.False(session.HasChildren);

        service.Push(DemoSessionService.MainSessionId, Child("child-1"));
        service.Push(DemoSessionService.MainSessionId, Child("child-2", "answered") with { Answer = "Three docs.", Steps = 3 });

        Assert.True(session.HasChildren);
        Assert.Equal("2 children · 1 running", session.ChildrenText);
        var row = session.Children[1];
        Assert.Equal(("answered · 3 steps", Tone.Green, "Three docs."), (row.Status, row.StatusTone, row.Output));
        Assert.StartsWith("↳ summarise the", row.Goal, StringComparison.Ordinal);
        Assert.Contains("8 steps · 64,000 tokens · 600 s", row.Details, StringComparison.Ordinal);
        Assert.Contains("generator qwen3.5-9b", row.Details, StringComparison.Ordinal);
        // The demo runs no children, so Stop has nothing to stop; it is offered for a running one only.
        Assert.True(session.Children[0].StopCommand.CanExecute(null));
        Assert.False(row.StopCommand.CanExecute(null));

        service.Push(DemoSessionService.MainSessionId, new ChildOfSession("cli-parent", "the goal"));
        Assert.Equal(("child of cli-parent", true), (session.ParentText, session.HasParent));
    }

    [Fact]
    public async Task The_board_shows_a_child_under_its_parents_card_not_as_a_card_of_its_own()
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell);
        var sessions = new DemoSessionService(dispatcher, time, DemoScene.Approval);
        using var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        await hub.StartAsync(null, DemoShell.CurrentSessionId, Ct);
        shell.Navigate(Area.Board);
        using var board = new BoardViewModel(shell, hub, new DemoBoardService(time), new SessionServiceWatcher(sessions), time, dispatcher);

        sessions.Announce(new SessionSummary("child-1", "summarise the docs")
        {
            ParentId = "cli-9f2e",
            Child = Child("child-1", "answered") with { Answer = "Three docs." },
            ReadOnly = true,
            LastActivity = time.GetUtcNow(),
        });

        Assert.DoesNotContain(board.Tiles.OfType<SessionCardViewModel>(), card => card.Id == "child-1");
        var parent = board.Cards["cli-9f2e"];
        Assert.True(parent.HasChildren);
        Assert.Equal("1 child session", parent.ChildrenHeader);
        var row = Assert.Single(parent.Children);
        Assert.Equal(("child-1", "answered", "Three docs."), (row.Id, row.Status, row.Output));
        // A child whose parent is not listed is a card of its own.
        sessions.Announce(new SessionSummary("child-2", "orphan") { ParentId = "cli-gone", Child = Child("child-2"), ReadOnly = true });
        Assert.Contains(board.Tiles.OfType<SessionCardViewModel>(), card => card.Id == "child-2");
    }

    [Fact]
    public async Task The_approval_card_shows_the_spawns_plan()
    {
        var time = ManualTimeProvider.Demo();
        var service = new DemoSessionService(new InlineDispatcher(), time, DemoScene.Main);
        using var session = new SessionViewModel(DemoSessionService.MainSessionId, service, new InlineDispatcher(), time);
        await session.OpenAsync(Ct);

        service.Push(DemoSessionService.MainSessionId, new ApprovalRequested("a-9", "delegate_task", "delegate_task {\"goal\":\"x\"}", time.GetUtcNow().AddMinutes(5))
        {
            CallId = "c-9",
            Delegation = ["Child: summarise the docs", "  tools: read_file", "  budget: 8 steps, 64,000 tokens, 600 s"],
        });

        var card = Assert.Single(session.Blocks.OfType<ApprovalCardBlock>());
        Assert.Equal("Child: summarise the docs\n  tools: read_file\n  budget: 8 steps, 64,000 tokens, 600 s", card.DelegationText);
    }

    [Fact]
    public void M5_a_cards_child_tree_is_capped_with_the_running_children_kept_and_the_rest_counted()
    {
        var time = ManualTimeProvider.Demo();
        var card = new SessionCardViewModel(new SessionSummary("cli-p", "parent"), time.GetUtcNow(), TimeZoneInfo.Utc, _ => { }, _ => { }, () => { });
        // Twenty children, the oldest one still running.
        var children = Enumerable.Range(0, 20).Select(i => Child($"child-{i:00}", i == 0 ? ChildSessionChanged.Running : "answered")).ToList();

        card.SetChildren(children, _ => { }, null);

        Assert.Equal(SessionCardViewModel.MaxChildRows, card.Children.Count);
        Assert.Equal("+12 more", card.MoreChildren);
        Assert.Equal("20 child sessions · 1 running", card.ChildrenHeader);
        // In order: the running one, then the latest seven.
        Assert.Equal(["child-00", .. Enumerable.Range(13, 7).Select(i => $"child-{i:00}")], card.Children.Select(row => row.Id));

        card.SetChildren(children.Take(3).ToList(), _ => { }, null);
        Assert.Equal(3, card.Children.Count);
        Assert.Equal("", card.MoreChildren);
    }

    [Fact]
    public async Task Security_L5_a_cut_goal_on_the_spawn_card_can_be_shown_whole()
    {
        var time = ManualTimeProvider.Demo();
        var service = new DemoSessionService(new InlineDispatcher(), time, DemoScene.Main);
        using var session = new SessionViewModel(DemoSessionService.MainSessionId, service, new InlineDispatcher(), time);
        await session.OpenAsync(Ct);

        service.Push(DemoSessionService.MainSessionId, new ApprovalRequested("a-9", "delegate_task", "delegate_task {}", time.GetUtcNow().AddMinutes(5))
        {
            CallId = "c-9",
            Delegation = ["Child: look aaaa… (600 more characters)"],
            DelegationWhole = ["Child: look " + new string('a', 604) + "\u202E"],
        });

        var card = Assert.Single(session.Blocks.OfType<ApprovalCardBlock>());
        Assert.True(card.CanShowWholeGoal);
        Assert.EndsWith("(600 more characters)", card.DelegationText, StringComparison.Ordinal);
        card.ShowWholeGoalCommand.Execute(null);
        Assert.False(card.CanShowWholeGoal);
        Assert.StartsWith("Child: look aaaa", card.DelegationText, StringComparison.Ordinal);
        Assert.Contains(new string('a', 604), card.DelegationText, StringComparison.Ordinal);
        // Revealed: the hidden character shows, as on every card line.
        Assert.DoesNotContain("\u202E", card.DelegationText, StringComparison.Ordinal);
    }
}
