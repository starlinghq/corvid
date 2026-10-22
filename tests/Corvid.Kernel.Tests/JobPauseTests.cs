using System.Globalization;
using Corvid.Kernel.Jobs;
using Microsoft.Data.Sqlite;

namespace Corvid.Kernel.Tests;

/// <summary>Pausing and resuming jobs (C-035), and the schema version 2 migration that added them.</summary>
public sealed class JobPauseTests
{
    [Fact]
    public void A_paused_job_is_not_claimed_and_has_no_next_run()
    {
        using var fixture = new JobFixture();
        var job = fixture.Store.Add(new NewJob("inbox", "check the inbox", "every 30m"));

        Assert.True(fixture.Store.Pause(job.JobId));
        var paused = fixture.Store.Get(job.JobId)!;
        Assert.True(paused.Paused);
        Assert.Equal(JobState.Active, paused.State);
        Assert.Null(paused.NextRunAt);

        fixture.Time.Advance(TimeSpan.FromHours(3));
        Assert.Empty(fixture.Store.ClaimDue(fixture.Time.GetUtcNow()));
        // Still listed as an active job, and pausing twice is refused.
        Assert.Contains(fixture.Store.List(), listed => listed.JobId == job.JobId && listed.Paused);
        Assert.False(fixture.Store.Pause(job.JobId));
    }

    [Fact]
    public void Resume_starts_from_now_on_the_next_natural_tick()
    {
        using var fixture = new JobFixture();
        var interval = fixture.Store.Add(new NewJob("inbox", "check the inbox", "every 30m"));
        var cron = fixture.Store.Add(new NewJob("standup", "standup notes", "0 9 * * *"));
        fixture.Store.Pause(interval.JobId);
        fixture.Store.Pause(cron.JobId);
        fixture.Time.Advance(TimeSpan.FromHours(5));
        var now = fixture.Time.GetUtcNow();

        var resumed = fixture.Store.Resume(interval.JobId)!;
        Assert.False(resumed.Paused);
        Assert.Equal(now.AddMinutes(30), resumed.NextRunAt);
        Assert.Equal(now.AddMinutes(30), fixture.Store.Get(interval.JobId)!.NextRunAt);
        // The 09:00 tick passed while paused (it is 14:00): the next is tomorrow's, with no catch-up run.
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), fixture.Store.Resume(cron.JobId)!.NextRunAt);
        Assert.Empty(fixture.Store.ClaimDue(now));
        Assert.Null(fixture.Store.Resume(interval.JobId));
    }

    [Fact]
    public void A_one_shot_whose_time_passed_while_paused_runs_once_on_resume()
    {
        using var fixture = new JobFixture();
        var job = fixture.Store.Add(new NewJob("reminder", "remind me", "in 10m"));
        fixture.Store.Pause(job.JobId);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        var now = fixture.Time.GetUtcNow();

        Assert.Equal(now, fixture.Store.Resume(job.JobId)!.NextRunAt);
        var claimed = Assert.Single(fixture.Store.ClaimDue(now));
        Assert.Equal(JobState.Completed, claimed.Job.State);
        Assert.Empty(fixture.Store.ClaimDue(now.AddHours(1)));
    }

    [Fact]
    public void Unknown_removed_and_completed_jobs_cannot_be_paused_or_resumed_and_a_paused_job_can_be_removed()
    {
        using var fixture = new JobFixture();
        Assert.False(fixture.Store.Pause("job-none"));
        Assert.Null(fixture.Store.Resume("job-none"));

        var removed = fixture.Store.Add(new NewJob("a", "a", "every 30m"));
        fixture.Store.Remove(removed.JobId);
        Assert.False(fixture.Store.Pause(removed.JobId));
        Assert.Null(fixture.Store.Resume(removed.JobId));

        var once = fixture.Store.Add(new NewJob("b", "b", "in 1m"));
        fixture.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Single(fixture.Store.ClaimDue(fixture.Time.GetUtcNow()));
        Assert.False(fixture.Store.Pause(once.JobId));
        Assert.Null(fixture.Store.Resume(once.JobId));

        // An active job that is not paused has nothing to resume.
        var active = fixture.Store.Add(new NewJob("c", "c", "every 30m"));
        Assert.Null(fixture.Store.Resume(active.JobId));
        // A paused job is still active, so it can be removed, and a removed one does not come back on resume.
        Assert.True(fixture.Store.Pause(active.JobId));
        Assert.True(fixture.Store.Remove(active.JobId));
        Assert.Null(fixture.Store.Resume(active.JobId));
        Assert.Equal(JobState.Removed, fixture.Store.Get(active.JobId)!.State);
    }

    [Fact]
    public void A_version_1_database_is_migrated_keeping_its_jobs_unpaused()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-jobs-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "jobs.db");
        try
        {
            var next = new DateTimeOffset(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = VersionOneSchema + string.Create(CultureInfo.InvariantCulture, $"""
                    INSERT INTO jobs VALUES ('job-old', 'inbox', 'check the inbox', 'every 30m', 'every 1800s', 'UTC', NULL, 0, NULL, 0,
                                             {next.AddMinutes(-30).UtcTicks}, {next.UtcTicks}, 'active', NULL);
                    """);
                command.ExecuteNonQuery();
            }

            var time = new ManualTime(next);
            var store = new JobStore(path, TimeZoneInfo.Utc, time);
            var job = Assert.Single(store.List());
            Assert.Equal(("job-old", false, next), (job.JobId, job.Paused, job.NextRunAt!.Value));
            Assert.Equal(2, UserVersion(path));
            // Opening again is a no-op, and the migrated file pauses and claims like a new one.
            store = new JobStore(path, TimeZoneInfo.Utc, time);
            Assert.Single(store.ClaimDue(next));
            Assert.True(store.Pause("job-old"));
            Assert.Equal(2, UserVersion(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_newer_schema_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-jobs-v3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "jobs.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 3;";
                command.ExecuteNonQuery();
            }
            Assert.Throws<InvalidDataException>(() => new JobStore(path, TimeZoneInfo.Utc));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int UserVersion(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // The schema C-012 shipped, as version 1 wrote it.
    private const string VersionOneSchema = """
        CREATE TABLE jobs (
            job_id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            prompt TEXT NOT NULL,
            schedule TEXT NOT NULL,
            spec TEXT NOT NULL,
            time_zone TEXT NOT NULL,
            script TEXT,
            no_agent INTEGER NOT NULL CHECK (no_agent IN (0, 1)),
            origin_session TEXT,
            created_tainted INTEGER NOT NULL CHECK (created_tainted IN (0, 1)),
            created_at INTEGER NOT NULL,
            next_run_at INTEGER,
            state TEXT NOT NULL CHECK (state IN ('active', 'completed', 'removed')),
            last_run_at INTEGER
        );
        CREATE INDEX jobs_due ON jobs (state, next_run_at);

        CREATE TABLE job_runs (
            run_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL REFERENCES jobs (job_id),
            session_id TEXT NOT NULL UNIQUE,
            scheduled_for INTEGER NOT NULL,
            started_at INTEGER NOT NULL,
            finished_at INTEGER,
            status TEXT NOT NULL,
            wake_decision_id TEXT,
            script_exit_code INTEGER,
            output TEXT,
            detail TEXT
        );
        CREATE INDEX job_runs_by_job ON job_runs (job_id, started_at);

        PRAGMA user_version = 1;
        """;
}
