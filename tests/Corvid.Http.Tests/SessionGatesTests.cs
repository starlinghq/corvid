namespace Corvid.Http.Tests;

public sealed class SessionGatesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AGateIsGoneOnceItsRequestsFinish()
    {
        var gates = new SessionGates();
        var held = new List<IDisposable>();
        for (var i = 0; i < 50; i++)
            held.Add(await gates.EnterAsync("http-" + i, Token));
        Assert.Equal(50, gates.Count);

        foreach (var lease in held)
            lease.Dispose();

        Assert.Equal(0, gates.Count);
    }

    [Fact]
    public async Task TwoRequestsForOneSessionStillTakeTurns()
    {
        var gates = new SessionGates();
        var first = await gates.EnterAsync("http-a", Token);
        var second = gates.EnterAsync("http-a", Token);
        var other = await gates.EnterAsync("http-b", Token);

        await Task.Delay(50, Token);
        Assert.False(second.IsCompleted);
        first.Dispose();
        // The waiting request kept the gate alive, so it was let in rather than racing a new one.
        (await second.WaitAsync(TimeSpan.FromSeconds(5), Token)).Dispose();
        other.Dispose();

        Assert.Equal(0, gates.Count);
        (await gates.EnterAsync("http-a", Token)).Dispose();
    }

    [Fact]
    public async Task ARequestArrivingWhileAWaiterRunsStillWaits()
    {
        var gates = new SessionGates();
        var first = await gates.EnterAsync("http-a", Token);
        var second = gates.EnterAsync("http-a", Token);
        first.Dispose();
        var running = await second.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // The gate the second request holds must still be the session's gate, not one dropped when the first let go.
        var third = gates.EnterAsync("http-a", Token);
        await Task.Delay(50, Token);
        Assert.False(third.IsCompleted);
        running.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5), Token)).Dispose();

        Assert.Equal(0, gates.Count);
    }

    [Fact]
    public async Task ManyConcurrentRequestsNeverOverlapAndLeaveNothingBehind()
    {
        var gates = new SessionGates();
        var inside = new int[4];
        var overlapped = false;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            var session = i % 4;
            using (await gates.EnterAsync("http-" + session, Token))
            {
                if (Interlocked.Increment(ref inside[session]) > 1)
                    overlapped = true;
                await Task.Yield();
                Interlocked.Decrement(ref inside[session]);
            }
        }, Token)));

        Assert.False(overlapped);
        Assert.Equal(0, gates.Count);
    }

    [Fact]
    public async Task AWaitThatIsCancelledLeavesNothingBehind()
    {
        var gates = new SessionGates();
        var first = await gates.EnterAsync("http-a", Token);
        using var aborted = new CancellationTokenSource();
        var waiting = gates.EnterAsync("http-a", aborted.Token);

        await aborted.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, gates.Count);
        first.Dispose();

        Assert.Equal(0, gates.Count);
    }
}
