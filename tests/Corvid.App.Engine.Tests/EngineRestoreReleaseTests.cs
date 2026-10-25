using Corvid.App.Core.Rewind;
using Corvid.Host.Composition;
using AppScope = Corvid.App.Core.Sessions.RewindScope;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A4 verification: a Rewind restore that opened a session lets go of it after (M2) only when the window does not view it;
/// one the window views keeps its live actor, and the window lets go of it when it stops viewing it.
/// </summary>
public sealed class EngineRestoreReleaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARestoreOfASessionTheWindowViewsLeavesItOpenUntilTheWindowLetsGo()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        fixture.Write("story.txt", "one\n");
        var id = fixture.Session();
        await fixture.WriteTurnAsync(id, "call-1", "story.txt", "ONE\n", "capitalise the story");
        fixture.Say(id, "thanks");
        // Nothing of it open in the runtime: the restore opens it.
        Assert.True(await fixture.Runtime.ReleaseDriverAsync(id));
        using var rewind = fixture.Rewind();
        var latest = (await rewind.GetTimelineAsync(id, Ct)).Entries[0];
        var waiter = new SessionWaiter();
        var view = await fixture.Sessions.OpenAsync(id, waiter.Add, Ct);

        var outcome = await rewind.RestoreAsync(id, latest.CheckpointId, AppScope.ConversationOnly, Ct);

        Assert.False(outcome.Refused);
        // Viewed: the window's handle is the live one, never a disposed actor's.
        Assert.True(fixture.Runtime.TryGetSession(id, out var session));
        Assert.Equal(LockState.HeldHere, fixture.Runtime.Driver(id).State);
        Assert.Same(session, fixture.Runtime.OpenSession(id, "app"));

        view.Dispose();
        await fixture.Sessions.IdleAsync();
        Assert.False(fixture.Runtime.TryGetSession(id, out _));
        Assert.Equal(LockState.Free, fixture.Runtime.Driver(id).State);
    }
}
