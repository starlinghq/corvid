using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Sessions;
using Corvid.Host.Tests;
using Corvid.Kernel.Turns;
using ApprovalAnswer = Corvid.App.Core.Sessions.ApprovalAnswer;
using ApprovalRequested = Corvid.App.Core.Sessions.ApprovalRequested;
using ApprovalResolved = Corvid.App.Core.Sessions.ApprovalResolved;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A5 (plan §14, docs/plan/ui.md §4.13 and F18): child sessions in the window on the engine. The spawn's card shows the
/// child's plan; the parent's view lists its children as they start and end (live and on replay); a child's summary names
/// its parent and is read-only; a child opens read-only with its parent linked, and nothing is sent into it.
/// </summary>
public sealed class EngineChildTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Delegating = new()
    {
        ["Corvid:Tools:Delegate"] = "true",
        ["Corvid:Approvals:Mode"] = "Manual",
    };

    [Fact]
    public async Task A_delegated_child_reaches_the_parents_view_the_board_and_opens_read_only()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, """{"goal":"count the items","tools":["list_notes"],"max_steps":3}"""),
            Reply.Say("Two items."),
            Reply.Say("Your helper counted two.")));
        await using var fixture = await EngineFixture.StartAsync(models, Delegating);
        var parent = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(parent, waiter.Add, Ct);

        await service.SendAsync(parent, "ask a helper", Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>();
        Assert.Equal(DelegateTaskTool.ToolName, card.Tool);
        Assert.Contains("Child: count the items", card.Delegation);
        Assert.Contains("  tools: list_notes", card.Delegation);
        Assert.Contains(card.Delegation, line => line.StartsWith("  budget: 3 steps", StringComparison.Ordinal));
        await service.AnswerApprovalAsync(parent, card.ApprovalId, new ApprovalAnswer(ApprovalChoice.Once), Ct);
        var ended = await waiter.WaitAsync<ChildSessionChanged>(child => !child.IsRunning);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        // Live: started, then ended with its goal, toolset, budget and answer.
        Assert.Contains(waiter.Updates.OfType<ChildSessionChanged>(), child => child.IsRunning && child.Goal == "count the items");
        Assert.Equal(("answered", "Two items.", 3), (ended.Status, ended.Answer, ended.Budget!.Steps));
        Assert.Equal(["list_notes"], ended.Tools);
        var view = SessionFold.Replay(parent, [.. subscription.History, .. waiter.Updates]);
        var listed = Assert.Single(view.Children);
        Assert.Equal(ended.ChildId, listed.ChildId);

        // The child's summary: its parent, read-only, and how it ended (the Board shows it under its parent).
        var summaries = await service.ListAsync(Ct);
        var child = Assert.Single(summaries, summary => summary.Id == ended.ChildId);
        Assert.Equal(parent, child.ParentId);
        Assert.True(child.ReadOnly);
        Assert.Equal("answered", child.Child!.Status);
        Assert.Contains("child session of " + parent, child.Holder, StringComparison.Ordinal);

        // Opened: read-only, its parent linked; nothing is sent into it.
        var childWaiter = new SessionWaiter();
        using var childView = await service.OpenAsync(ended.ChildId, childWaiter.Add, Ct);
        var link = Assert.Single(childView.History.OfType<ChildOfSession>());
        Assert.Equal((parent, "count the items"), (link.ParentId, link.Goal));
        Assert.Contains(childView.History.OfType<DriverChanged>(), driver => driver.ReadOnly);
        var childState = SessionFold.Replay(ended.ChildId, childView.History);
        Assert.True(childState.ReadOnly);
        Assert.Equal(parent, childState.ParentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(ended.ChildId, "do more", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetGoalAsync(ended.ChildId, "a goal", Ct));

        // Replayed in a fresh window: the parent's children come back as they ended.
        using var again = fixture.Sessions();
        var replayed = new SessionWaiter();
        using var reopened = await again.OpenAsync(parent, replayed.Add, Ct);
        var back = Assert.Single(SessionFold.Replay(parent, reopened.History).Children);
        Assert.Equal(("answered", "Two items."), (back.Status, back.Answer));
    }

    [Fact]
    public async Task M3_a_child_the_person_starts_from_the_window_asks_on_the_parents_view_and_L4_its_card_reads_the_childs_log()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Say("noted"),
            // The child: one call that asks (Manual), then its answer.
            Reply.Call("add_note", """{"text":"from the child"}"""),
            Reply.Say("Added.")));
        await using var fixture = await EngineFixture.StartAsync(models, Delegating);
        var session = fixture.Runtime.OpenSession(surface: "app");
        // The parent holds outside content, so the child starts tainted, and its own call's approval is logged so.
        await (await session.PostAsync("forwarded from someone", EnvelopeOrigin.External, Ct)).WaitAsync(Ct);
        var parent = session.Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(parent, waiter.Add, Ct);

        // No turn of the parent runs: the command starts the child and returns.
        await service.RunCommandAsync(parent, "/delegate --tools add_note add a note", Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>(request => request.Tool == "add_note");
        Assert.Contains(card.Delegation, line => line.StartsWith("Asked by child session ", StringComparison.Ordinal));
        // L4: the card's facts are the child's (its logged request says it holds outside content), not the parent's.
        Assert.NotNull(card.TaintSource);
        await service.AnswerApprovalAsync(parent, card.ApprovalId, new ApprovalAnswer(ApprovalChoice.Once), Ct);
        var ended = await waiter.WaitAsync<ChildSessionChanged>(child => !child.IsRunning);

        Assert.Equal(("answered", "Added."), (ended.Status, ended.Answer));
        Assert.Contains("from the child", models.Tools.Notes);
        Assert.Contains(waiter.Updates.OfType<ApprovalResolved>(), resolved => resolved.ApprovalId == card.ApprovalId && resolved.Outcome == ApprovalOutcome.Approved);
    }

    [Fact]
    public void L1_a_replayed_child_that_nothing_runs_and_that_never_ended_is_unfinished()
    {
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        ChildStarted Start(string id) => new(new ChildSpawned(id, "a goal", [], new ChildBudget(3, 1000, 60), "agent"), 4) { At = at };
        var translator = new SessionUpdateTranslator(Path.GetTempPath()) { IsChildRunning = id => id == "child-live" };

        var dead = Assert.IsType<ChildSessionChanged>(Assert.Single(translator.Translate(Start("child-dead"))));
        var live = Assert.IsType<ChildSessionChanged>(Assert.Single(translator.Translate(Start("child-live"))));

        Assert.Equal("unfinished", dead.Status);
        Assert.False(dead.IsRunning);
        Assert.True(live.IsRunning);
        // Live (runtime.ChildChanged), a start is running.
        Assert.True(Assert.IsType<ChildSessionChanged>(Assert.Single(translator.Child(Start("child-new")))).IsRunning);
    }

    [Fact]
    public async Task Security_L5_a_spawn_card_with_a_long_goal_carries_the_whole_goal_to_show()
    {
        var goal = "look " + new string('a', 1_000);
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, System.Text.Json.JsonSerializer.Serialize(new { goal })),
            Reply.Say("ok")));
        await using var fixture = await EngineFixture.StartAsync(models, Delegating);
        var parent = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(parent, waiter.Add, Ct);

        await service.SendAsync(parent, "ask a helper", Ct);
        var card = await waiter.WaitAsync<ApprovalRequested>();

        Assert.EndsWith("more characters)", card.Delegation[0], StringComparison.Ordinal);
        Assert.Equal("Child: " + goal, card.DelegationWhole[0]);
        await service.AnswerApprovalAsync(parent, card.ApprovalId, new ApprovalAnswer(ApprovalChoice.Deny), Ct);
        await waiter.WaitAsync<TurnEnded>();
    }
}
