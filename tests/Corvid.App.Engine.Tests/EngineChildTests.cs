using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Kernel.Turns;

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
}
