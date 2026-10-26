using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Models;

namespace Corvid.App.Engine.Tests;

/// <summary>A5: a session's model switches (any slot, any reason) reach its view on the engine.</summary>
public sealed class EngineSlotModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_choice_reaches_the_view_as_a_slot_change_and_the_generators_row()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        var session = fixture.Runtime.OpenSession(id, "app");

        await session.SetModelAsync(ModelSlots.Generator, "fake-gen-large", cancellationToken: Ct);
        await session.SetModelAsync(ModelSlots.Decider, "fake-kev", cancellationToken: Ct);

        var row = await waiter.WaitAsync<ModelSwitched>();
        Assert.Equal((ModelSlots.Generator, "fake-gen", "fake-gen-large", ModelSwitchKind.Switched, "for this session"),
            (row.Slot, row.From, row.To, row.Kind, row.Reason));
        var generator = await waiter.WaitAsync<SlotModelChanged>(change => change.Slot == ModelSlots.Generator);
        Assert.Equal(("fake-gen-large@cpu", "session", true), (generator.To, generator.Reason, generator.SessionDefault));
        var decider = await waiter.WaitAsync<SlotModelChanged>(change => change.Slot == ModelSlots.Decider);
        Assert.Equal(("none", "fake-kev@cpu"), (decider.From, decider.To));
        // A5 (switch): the person's decider choice has its row too, naming its slot.
        Assert.Equal([ModelSlots.Generator, ModelSlots.Decider], waiter.Updates.OfType<ModelSwitched>().Select(row => row.Slot));

        var view = SessionFold.Replay(id, waiter.Updates);
        Assert.Equal("fake-gen-large", view.Model);
        Assert.Equal("fake-kev@cpu", view.SlotModels[ModelSlots.Decider].To);
    }

    [Fact]
    public async Task A_turn_switch_returns_when_the_turn_ends_and_the_view_says_so()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        var session = fixture.Runtime.OpenSession(id, "app");

        await session.SetModelAsync(ModelSlots.Generator, "fake-gen-large", SwitchScope.Turn, Ct);
        await service.SendAsync(id, "hello", Ct);
        var back = await waiter.WaitAsync<ModelSwitched>(row => row.Kind == ModelSwitchKind.Returned);
        await service.IdleAsync();

        Assert.Equal(("fake-gen-large", "fake-gen", "the turn ended"), (back.From, back.To, back.Reason));
        Assert.Equal(ModelSwitchKind.Switched, waiter.Updates.OfType<ModelSwitched>().First().Kind);
    }
}
