using Corvid.Host.Composition;

namespace Corvid.Host.Tests;

// The replay behind CorvidRuntime.JobCompleted (C-039), without a scheduler or a model.
public sealed class ReplayingEventTests
{
    [Fact]
    public void ALateHandlerIsReplayedTheMostRecentRaisesInOrder()
    {
        var capacity = CorvidRuntime.JobCompletedReplay;
        var replaying = new ReplayingEvent<int, string>(capacity);
        for (var i = 0; i <= capacity; i++)
            replaying.Raise(i, $"run {i}");

        var heard = new List<(int, string)>();
        replaying.Add((n, s) => heard.Add((n, s)));

        // capacity + 1 raised: the oldest was evicted, the rest replayed oldest first.
        Assert.Equal(Enumerable.Range(1, capacity).Select(i => (i, $"run {i}")), heard);
    }

    [Fact]
    public void AHandlerHearsLaterRaisesLiveAndNotAgainOnAnotherAdd()
    {
        var replaying = new ReplayingEvent<int, string>(4);
        replaying.Raise(1, "a");
        var first = new List<int>();
        replaying.Add((n, _) => first.Add(n));
        replaying.Raise(2, "b");
        var second = new List<int>();
        replaying.Add((n, _) => second.Add(n));

        Assert.Equal([1, 2], first);
        Assert.Equal([1, 2], second);
    }

    [Fact]
    public void ARemovedHandlerHearsNothingMore()
    {
        var replaying = new ReplayingEvent<int, string>(4);
        var heard = new List<int>();
        void Handler(int n, string _) => heard.Add(n);
        replaying.Add(Handler);
        replaying.Raise(1, "a");
        replaying.Remove(Handler);
        replaying.Raise(2, "b");

        Assert.Equal([1], heard);
    }

    [Fact]
    public void AHandlerAddedWhileRaisesAreInFlightHearsEachExactlyOnce()
    {
        const int raises = 16;
        for (var attempt = 0; attempt < 2_000; attempt++)
        {
            var replaying = new ReplayingEvent<int, string>(raises);
            var counts = new int[raises];
            using var start = new Barrier(2);
            var raiser = new Thread(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < raises; i++)
                    replaying.Raise(i, "");
            });
            raiser.Start();
            start.SignalAndWait(TestContext.Current.CancellationToken);
            replaying.Add((n, _) => Interlocked.Increment(ref counts[n]));
            raiser.Join();

            // Every raise fits the buffer, so each is replayed or heard live: once, whichever side of the add it fell.
            Assert.All(counts, count => Assert.Equal(1, count));
        }
    }

    [Fact]
    public void TheCapacityMustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayingEvent<int, string>(0));
}
