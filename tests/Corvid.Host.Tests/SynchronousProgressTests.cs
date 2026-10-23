using Corvid.Host.Models;

namespace Corvid.Host.Tests;

public sealed class SynchronousProgressTests
{
    [Fact]
    public void AReportIsHandledOnTheReportingThreadBeforeReportReturns()
    {
        var thread = -1;
        var value = 0;
        var progress = new SynchronousProgress<int>(item =>
        {
            thread = Environment.CurrentManagedThreadId;
            value = item;
        });

        progress.Report(7);

        Assert.Equal(Environment.CurrentManagedThreadId, thread);
        Assert.Equal(7, value);
    }

    [Fact]
    public async Task ConcurrentReportsNeverOverlap()
    {
        var inside = 0;
        var overlapped = 0;
        var handled = 0;
        var progress = new SynchronousProgress<int>(_ =>
        {
            if (Interlocked.Increment(ref inside) > 1)
                Interlocked.Increment(ref overlapped);
            Thread.SpinWait(2_000);
            Interlocked.Decrement(ref inside);
            handled++; // Not atomic on purpose: the gate is what keeps the count exact.
        });

        using var start = new ManualResetEventSlim();
        var reporters = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 200; i++)
                progress.Report(i);
        }, TestContext.Current.CancellationToken)).ToArray();
        start.Set();
        await Task.WhenAll(reporters);

        Assert.Equal(0, overlapped);
        Assert.Equal(8 * 200, handled);
    }
}
