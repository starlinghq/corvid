using System.Globalization;
using Corvid.Fabric;
using Corvid.Kernel.Jobs;

namespace Corvid.Kernel.Tests;

/// <summary>
/// C-041: the store's run history across jobs, its in-process <see cref="JobStore.Changed"/> event, and the scheduler waking
/// on it (or on <see cref="JobScheduler.Wake"/>) instead of sleeping out its poll interval.
/// </summary>
public sealed class JobWakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Recent_runs_are_newest_first_across_jobs_and_limited()
    {
        using var fixture = new JobFixture();
        var a = fixture.Store.Add(new NewJob("a", "a", "every 10m"));
        var b = fixture.Store.Add(new NewJob("b", "b", "every 20m"));
        Assert.Empty(fixture.Store.RecentRuns());

        var started = new List<(string Job, DateTimeOffset At)>();
        for (var i = 0; i < 6; i++)
        {
            fixture.Time.Advance(TimeSpan.FromMinutes(10));
            foreach (var claimed in fixture.Store.ClaimDue(fixture.Time.GetUtcNow()))
                started.Add((claimed.Job.JobId, claimed.Run.StartedAt));
        }
        // a every 10 minutes (6 runs), b every 20 (3 runs).
        Assert.Equal(9, started.Count);

        var all = fixture.Store.RecentRuns();
        Assert.Equal(9, all.Count);
        Assert.Equal(all.OrderByDescending(run => run.StartedAt).ThenByDescending(run => run.RunId, StringComparer.Ordinal).Select(run => run.RunId),
            all.Select(run => run.RunId));
        Assert.Equal(fixture.Time.GetUtcNow(), all[0].StartedAt);
        Assert.Contains(all, run => run.JobId == a.JobId);
        Assert.Contains(all, run => run.JobId == b.JobId);

        var three = fixture.Store.RecentRuns(3);
        Assert.Equal(all.Take(3).Select(run => run.RunId), three.Select(run => run.RunId));
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Store.RecentRuns(0));
    }

    [Fact]
    public void Changed_is_raised_only_by_writes_that_changed_a_job()
    {
        using var fixture = new JobFixture();
        var changes = 0;
        fixture.Store.Changed += () => changes++;

        var job = fixture.Store.Add(new NewJob("inbox", "check the inbox", "every 30m"));
        Assert.Equal(1, changes);
        Assert.True(fixture.Store.Pause(job.JobId));
        Assert.False(fixture.Store.Pause(job.JobId));
        Assert.Equal(2, changes);
        Assert.NotNull(fixture.Store.Resume(job.JobId));
        Assert.Null(fixture.Store.Resume(job.JobId));
        Assert.Equal(3, changes);
        Assert.Throws<FormatException>(() => fixture.Store.Add(new NewJob("bad", "x", "whenever")));
        Assert.Equal(3, changes);

        // Claiming and finishing runs are the scheduler's own writes: no change to wake it with.
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var claimed = Assert.Single(fixture.Store.ClaimDue(fixture.Time.GetUtcNow()));
        fixture.Store.Finish(claimed.Run.RunId, JobRunStatus.Ran, fixture.Time.GetUtcNow());
        Assert.Equal(3, changes);

        Assert.True(fixture.Store.Remove(job.JobId));
        Assert.False(fixture.Store.Remove(job.JobId));
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task A_job_added_or_resumed_in_this_process_or_a_wake_runs_without_waiting_a_poll_interval()
    {
        using var fixture = new JobFixture();
        // Real time, and a poll interval far longer than the test may take: a run within seconds shows the wake worked.
        var path = Path.Combine(fixture.Root, "live.db");
        var store = new JobStore(path, TimeZoneInfo.Utc);
        var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new KeywordEmbedder("inbox"), new FakeGenerator()), fixture.Log);
        var scheduler = new JobScheduler(store, fabric, agent: new FakeAgent(),
            options: new JobSchedulerOptions { PollInterval = TimeSpan.FromMinutes(10) });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = Task.Run(() => scheduler.RunAsync(stop.Token), Ct);
        try
        {
            // Nothing is scheduled yet, so the loop is asleep for its whole poll interval.
            await Task.Delay(300, Ct);
            var added = store.Add(new NewJob("soon", "say hi", At(2)));
            await RanAsync(store, added.JobId);

            // A one-shot paused before its time, resumed after it: Resume sets its next run to now, and Changed wakes the loop.
            var once = store.Add(new NewJob("once", "say hi again", At(2)));
            Assert.True(store.Pause(once.JobId));
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.Empty(store.Runs(once.JobId));
            Assert.NotNull(store.Resume(once.JobId));
            await RanAsync(store, once.JobId);

            // Written through another instance (as another process would), so only Wake ends the sleep.
            var elsewhere = new JobStore(path, TimeZoneInfo.Utc).Add(new NewJob("elsewhere", "say hi from afar", At(1)));
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Assert.Empty(store.Runs(elsewhere.JobId));
            scheduler.Wake();
            await RanAsync(store, elsewhere.JobId);
        }
        finally
        {
            await stop.CancelAsync();
            await loop;
        }
        Assert.False(loop.IsFaulted);
    }

    private static string At(int seconds) =>
        DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static async Task RanAsync(JobStore store, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (store.Runs(jobId) is not [{ FinishedAt: not null }])
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"{jobId} did not run within 60 s.");
            await Task.Delay(50, Ct);
        }
    }
}
