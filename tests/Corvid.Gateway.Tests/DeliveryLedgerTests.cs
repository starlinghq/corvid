using Corvid.Abstractions.Events;

namespace Corvid.Gateway.Tests;

public sealed class DeliveryLedgerTests
{
    [Fact]
    public async Task OnlyOpenDeliveriesAreHeldAndEachIsResolvedOnce()
    {
        await using var fixture = new GatewayFixture();
        var ledger = new DeliveryLedger(fixture.Log);

        var sent = ledger.Queue(new DeliveryQueued("d-1", "fake", "c1", "first", "reply"));
        ledger.Resolve("d-1", DeliveryStatus.Delivered, "m1");
        // A later read passes the queued entry again and then its resolution: nothing is left open.
        ledger.Catch();
        ledger.Resolve("d-1", DeliveryStatus.Failed, detail: "again");
        Assert.Empty(ledger.Pending);
        Assert.Equal(sent.Sequence + 1, ledger.LowWater);
        Assert.Single(fixture.Ledger(), stored => stored.Payload is DeliveryResolved);

        // Queued by another process: open once read, in ledger order with this process's own.
        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("d-2", "fake", "c1", "second", "job"));
        ledger.Queue(new DeliveryQueued("d-3", "fake", "c1", "third", "reply"));
        ledger.Catch();
        Assert.Equal(["d-2", "d-3"], ledger.Pending.Select(pending => pending.Delivery.DeliveryId));
        Assert.Equal(sent.Sequence + 1, ledger.LowWater);

        // A fresh reader from the stored mark sees exactly what is still open.
        var restarted = new DeliveryLedger(fixture.Log);
        restarted.Catch(ledger.LowWater);
        Assert.Equal(["d-2", "d-3"], restarted.Pending.Select(pending => pending.Delivery.DeliveryId));
    }

    [Fact]
    public async Task TheMarkNeverPassesAnEntryNotReadYet()
    {
        await using var fixture = new GatewayFixture();
        var ledger = new DeliveryLedger(fixture.Log);
        ledger.Catch();

        // Another process queues a job result; before the gateway reads it, it queues its own reply, which stays open (a
        // failed send). The stored mark must not pass the job result, or a restart would never send it.
        DeliveryLedger.Queue(fixture.Log, new DeliveryQueued("job-1", "fake", "c1", "job result", "job"));
        ledger.Queue(new DeliveryQueued("d-1", "fake", "c1", "reply", "reply"));
        Assert.Equal(["d-1"], ledger.Pending.Select(pending => pending.Delivery.DeliveryId));

        var restarted = new DeliveryLedger(fixture.Log);
        restarted.Catch(ledger.LowWater);
        Assert.Equal(["job-1", "d-1"], restarted.Pending.Select(pending => pending.Delivery.DeliveryId));
    }
}
