using Corvid.App.Core.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// M2 (A4 review): the window lets go of the driver lock of a session it drove once it stops viewing it and nothing of it
/// runs or waits here, so a terminal's <c>corvid chat --session</c> can drive it; sending from the window again takes it back.
/// A second runtime over the same data directory stands in for the terminal.
/// </summary>
public sealed class EngineDriverReleaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASessionTheWindowStopsViewingIsLetGoOfAndATerminalCanDriveIt()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("Hello."), Reply.Say("Again."))));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        await using var terminal = await fixture.Host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Ct);
        var waiter = new SessionWaiter();
        using (await service.OpenAsync(id, waiter.Add, Ct))
        {
            await service.SendAsync(id, "hi there", Ct);
            await waiter.WaitAsync<TurnEnded>(_ => true);
            await service.IdleAsync();
            // Viewed and driven here: the terminal is refused.
            Assert.Equal(LockState.HeldHere, fixture.Runtime.Driver(id).State);
            Assert.Throws<SessionHeldException>(() => terminal.OpenSession(id));
        }
        await service.IdleAsync();

        Assert.False(fixture.Runtime.TryGetSession(id, out _));
        Assert.Equal(LockState.Free, terminal.Driver(id).State);
        terminal.OpenSession(id);
        Assert.Equal(LockState.HeldElsewhere, fixture.Runtime.Driver(id).State);
    }
}
