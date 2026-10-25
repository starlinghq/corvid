using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;

namespace Corvid.Kernel.Tests;

/// <summary>
/// A4 verification: a session is let go of (its driver lock released, its actor disposed) only when no message posted to
/// it is still on its way through the host's ingress, where its actor looks idle until the router hands the message over.
/// </summary>
public sealed class SessionHostReleaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_with_a_message_in_the_ingress_is_not_let_go_of_until_it_is_routed()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("routed")));
        var sink = new CollectingSink();
        using var routerHeld = new ManualResetEventSlim();
        // The router is held up building another session's actor, so the message posted after it waits in the ingress.
        await using var host = new SessionHost(id =>
        {
            if (id == TurnHarness.Session)
                return harness.Actor(sink);
            routerHeld.Wait(Ct);
            throw new InvalidOperationException("no such session");
        });
        var actor = host.GetOrCreate(TurnHarness.Session);
        Assert.True(actor.IsIdle);

        await host.PostAsync(new Envelope("s-other", "cli", "hold the router"), Ct);
        await host.PostAsync(new Envelope(TurnHarness.Session, "cli", "hello"), Ct);

        // Idle by its actor's measure, but a message is on its way: not let go of.
        Assert.False(host.TryRemoveIdle(TurnHarness.Session, found => found.IsIdle, out var removed));
        Assert.Null(removed);
        Assert.True(host.TryGet(TurnHarness.Session, out var same));
        Assert.Same(actor, same);

        routerHeld.Set();
        Assert.Equal("routed", (await sink.NextAsync()).Result!.Text);
        // Routed and answered: now it can go.
        var released = false;
        for (var i = 0; i < 500 && !(released = host.TryRemoveIdle(TurnHarness.Session, found => found.IsIdle, out removed)); i++)
            await Task.Delay(10, Ct);
        Assert.True(released);
        Assert.Same(actor, removed);
        await removed!.DisposeAsync();
    }
}
