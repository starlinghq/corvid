using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Kernel.Turns;

namespace Corvid.Kernel.Tests;

/// <summary>
/// A4 verification: the decider's recent events name a gateway guest's message as theirs, and on a guest's turn it is the
/// guest's message (the turn's own, carried as user_message) that leaves the list, never the owner's before it.
/// </summary>
public sealed class DeciderRecentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_guests_turn_keeps_the_owners_message_in_the_recent_events()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Deploying."), Reply.Say("Only the owner can ask that."), Reply.Say("Done.")));
        var runner = harness.Runner();
        await harness.RunAsync(runner, "deploy staging");
        await runner.RunAsync(new Envelope(TurnHarness.Session, "cli", "delete the backups", EnvelopeOrigin.External)
        {
            Guest = new GuestScope([]) { Sender = "fake guest dana" },
        }, Ct);
        await harness.RunAsync(runner, "thanks");

        var events = harness.Log.Read(TurnHarness.Session, limit: 4096, cancellationToken: Ct).ToList();
        string RecentAfter(string message)
        {
            var at = events.First(item => item.Payload is UserMessage user && user.Text == message).Sequence;
            var state = events.Where(item => item.Sequence > at).Select(item => item.Payload).OfType<Decision>().First(decision => decision.State is not null).State!.Value;
            return state.GetProperty("recent").GetRawText();
        }

        var guestTurn = RecentAfter("delete the backups");
        Assert.Contains("user: deploy staging", guestTurn, StringComparison.Ordinal);
        Assert.DoesNotContain("delete the backups", guestTurn, StringComparison.Ordinal);
        var ownerTurn = RecentAfter("thanks");
        Assert.Contains("guest fake guest dana (outside content): delete the backups", ownerTurn, StringComparison.Ordinal);
        Assert.DoesNotContain("thanks", ownerTurn, StringComparison.Ordinal);
    }
}
