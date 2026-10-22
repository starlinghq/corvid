using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Corvid.Kernel.Jobs;

public enum JobState
{
    Active,
    /// <summary>A one-shot job that has run.</summary>
    Completed,
    Removed,
}

/// <summary>A durable scheduled job (plan §13).</summary>
/// <param name="Schedule">The schedule as written.</param>
/// <param name="Spec">The canonical schedule (<see cref="JobSchedule.Canonical"/>), which is what runs.</param>
/// <param name="Prompt">What the agent is asked on each run; empty for a <paramref name="NoAgent"/> job.</param>
/// <param name="Script">A shell command run in the sandbox before each run; its output feeds <c>wake</c>.</param>
/// <param name="NoAgent">Runs the script only and records its output; no model is used.</param>
/// <param name="OriginSession">The session that created the job, if any.</param>
/// <param name="CreatedTainted">
/// The job was created while that session held outside content, so its prompt may carry someone else's instructions:
/// its runs are tainted (plan §10.3).
/// </param>
/// <param name="NextRunAt">Null once a one-shot job has run, while a job is paused, or once it is removed.</param>
public sealed record ScheduledJob(
    string JobId,
    string Name,
    string Prompt,
    string Schedule,
    string Spec,
    string TimeZoneId,
    string? Script,
    bool NoAgent,
    string? OriginSession,
    bool CreatedTainted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextRunAt,
    JobState State,
    DateTimeOffset? LastRunAt)
{
    public JobSchedule ParsedSchedule => JobSchedule.FromCanonical(Spec, JobStore.FindZone(TimeZoneId));

    /// <summary>
    /// The user paused the job (<see cref="JobStore.Pause"/>): it stays active but is never claimed, and has no next run,
    /// until it is resumed.
    /// </summary>
    public bool Paused { get; init; }
}

/// <summary>What a caller asks to schedule.</summary>
public sealed record NewJob(string Name, string Prompt, string Schedule, string? Script = null, bool NoAgent = false,
    string? OriginSession = null, bool CreatedTainted = false);

/// <summary>Run statuses as stored. They are persisted names.</summary>
public static class JobRunStatus
{
    public const string Running = "running";
    /// <summary>The agent ran in the run's session.</summary>
    public const string Ran = "ran";
    /// <summary>A no-agent job's script ran.</summary>
    public const string ScriptOnly = "script_only";
    /// <summary><c>wake</c> did not clear its floor: the tick was skipped and logged.</summary>
    public const string Skipped = "skipped";
    /// <summary>The approval pipeline or the sandbox refused the pre-run script.</summary>
    public const string ScriptRefused = "script_refused";
    public const string Failed = "failed";
    /// <summary>Corvid stopped while the run was in progress.</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>One run of a job. Each run that reaches a model is a fresh session (<paramref name="SessionId"/>), linked here.</summary>
/// <param name="WakeDecisionId">The <c>wake</c> decision in the run's session, when one was asked.</param>
/// <param name="Output">The script's output for a no-agent job, or the agent's answer.</param>
public sealed record JobRun(
    string RunId,
    string JobId,
    string SessionId,
    DateTimeOffset ScheduledFor,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string? WakeDecisionId,
    int? ScriptExitCode,
    string? Output,
    string? Detail);

/// <summary>A run claimed by <see cref="JobStore.ClaimDue"/>, with its job as it was when claimed.</summary>
public sealed record ClaimedRun(ScheduledJob Job, JobRun Run);

/// <summary>
/// Durable jobs and their runs in their own SQLite file (WAL, <c>synchronous=FULL</c>), separate from the append-only
/// event log because jobs change. Times are UTC ticks. A run is claimed (its job's next time moved on, the run recorded
/// as running) in one transaction before it starts, so a crash never runs a job twice for the same tick; it leaves a run
/// that <see cref="InterruptStale"/> marks interrupted.
/// </summary>
/// <remarks>SQLite calls are synchronous. Call from a storage worker if the caller must not block.</remarks>
public sealed class JobStore
{
    private const int SchemaVersion = 2;

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    /// <param name="zone">The zone new jobs' wall-clock times are read in. Default: the machine's.</param>
    public JobStore(string path, TimeZoneInfo? zone = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:")
            throw new ArgumentException("The job store requires an on-disk database.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        TimeZone = zone ?? TimeZoneInfo.Local;
        _time = time ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is not (0 or 1 or SchemaVersion))
            throw new InvalidDataException($"Unsupported job database schema version {version}.");
        if (version == 0)
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }
        else if (version == 1)
        {
            // Version 2 adds pausing as its own column: the state CHECK could change only through a table rebuild, which
            // job_runs' foreign key makes awkward. It runs in the same immediate transaction as the version check, so two
            // processes starting together do not both migrate.
            command.CommandText = MigrateToVersion2;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string Path { get; }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>Schedules a job. Its schedule is parsed now, so a delay counts from this moment.</summary>
    /// <exception cref="FormatException">The schedule is not valid (the message says why, for the model).</exception>
    /// <exception cref="ArgumentException">The job has neither a prompt nor, for a no-agent job, a script.</exception>
    public ScheduledJob Add(NewJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.Name);
        ArgumentNullException.ThrowIfNull(job.Prompt);
        if (job.NoAgent && string.IsNullOrWhiteSpace(job.Script))
            throw new ArgumentException("A no-agent job runs only its script, so it needs one.", nameof(job));
        if (!job.NoAgent && string.IsNullOrWhiteSpace(job.Prompt))
            throw new ArgumentException("A job needs a prompt for the agent (or no_agent with a script).", nameof(job));
        var now = _time.GetUtcNow();
        var schedule = JobSchedule.Parse(job.Schedule, now, TimeZone);
        var first = schedule.First(now) ?? throw new FormatException($"'{job.Schedule}' never runs.");
        var created = new ScheduledJob("job-" + Guid.NewGuid().ToString("N")[..12], job.Name.Trim(), job.NoAgent ? "" : job.Prompt.Trim(),
            job.Schedule.Trim(), schedule.Canonical, TimeZone.Id, string.IsNullOrWhiteSpace(job.Script) ? null : job.Script, job.NoAgent,
            job.OriginSession, job.CreatedTainted, now, first, JobState.Active, null);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (job_id, name, prompt, schedule, spec, time_zone, script, no_agent, origin_session, created_tainted,
                              created_at, next_run_at, state, last_run_at)
            VALUES ($id, $name, $prompt, $schedule, $spec, $zone, $script, $no_agent, $origin, $tainted, $created, $next, $state, NULL);
            """;
        command.Parameters.AddWithValue("$id", created.JobId);
        command.Parameters.AddWithValue("$name", created.Name);
        command.Parameters.AddWithValue("$prompt", created.Prompt);
        command.Parameters.AddWithValue("$schedule", created.Schedule);
        command.Parameters.AddWithValue("$spec", created.Spec);
        command.Parameters.AddWithValue("$zone", created.TimeZoneId);
        command.Parameters.AddWithValue("$script", (object?)created.Script ?? DBNull.Value);
        command.Parameters.AddWithValue("$no_agent", created.NoAgent ? 1 : 0);
        command.Parameters.AddWithValue("$origin", (object?)created.OriginSession ?? DBNull.Value);
        command.Parameters.AddWithValue("$tainted", created.CreatedTainted ? 1 : 0);
        command.Parameters.AddWithValue("$created", now.UtcTicks);
        command.Parameters.AddWithValue("$next", first.UtcTicks);
        command.Parameters.AddWithValue("$state", StateName(JobState.Active));
        command.ExecuteNonQuery();
        return created;
    }

    public ScheduledJob? Get(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM jobs WHERE job_id = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    /// <summary>Jobs in creation order: active ones, and with <paramref name="includeFinished"/> completed and removed ones too.</summary>
    public IReadOnlyList<ScheduledJob> List(bool includeFinished = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {JobColumns} FROM jobs {(includeFinished ? "" : "WHERE state = 'active'")} ORDER BY created_at, job_id;";
        using var reader = command.ExecuteReader();
        var jobs = new List<ScheduledJob>();
        while (reader.Read())
            jobs.Add(ReadJob(reader));
        return jobs;
    }

    /// <summary>Stops a job from running again. Its runs stay. False when there is no active job with that id.</summary>
    public bool Remove(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET state = 'removed', next_run_at = NULL WHERE job_id = $id AND state = 'active';";
        command.Parameters.AddWithValue("$id", jobId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Pauses an active job: it keeps its state but is not claimed, and its next run is cleared so nothing waits for it.
    /// False when there is no active, unpaused job with that id.
    /// </summary>
    public bool Pause(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET paused = 1, next_run_at = NULL WHERE job_id = $id AND state = 'active' AND paused = 0;";
        command.Parameters.AddWithValue("$id", jobId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Resumes a paused job from now: a recurring job on its next natural tick (ticks missed while paused are not run), a
    /// one-shot at its time, or now when that time passed while it was paused, so it runs once. Null when there is no
    /// paused active job with that id.
    /// </summary>
    public ScheduledJob? Resume(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var now = _time.GetUtcNow();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        ScheduledJob? job;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {JobColumns} FROM jobs WHERE job_id = $id AND state = 'active' AND paused = 1;";
            select.Parameters.AddWithValue("$id", jobId);
            using var reader = select.ExecuteReader();
            job = reader.Read() ? ReadJob(reader) : null;
        }
        if (job is null)
            return null;

        // A schedule with no time left (a cron that never matches again) resumes with nothing to run.
        var next = job.ParsedSchedule.First(now);
        if (next < now)
            next = now;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE jobs SET paused = 0, next_run_at = $next WHERE job_id = $id;";
            update.Parameters.AddWithValue("$next", next is { } at ? at.UtcTicks : DBNull.Value);
            update.Parameters.AddWithValue("$id", jobId);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return job with { NextRunAt = next, Paused = false };
    }

    /// <summary>
    /// Claims every active job due at <paramref name="now"/>: moves its next time on (missed ticks collapse into this one)
    /// or completes a one-shot, and records a running run with a fresh session id, all in one transaction.
    /// </summary>
    public IReadOnlyList<ClaimedRun> ClaimDue(DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var due = new List<ScheduledJob>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"SELECT {JobColumns} FROM jobs WHERE state = 'active' AND paused = 0 AND next_run_at <= $now ORDER BY next_run_at, job_id;";
            select.Parameters.AddWithValue("$now", now.UtcTicks);
            using var reader = select.ExecuteReader();
            while (reader.Read())
                due.Add(ReadJob(reader));
        }

        var claimed = new List<ClaimedRun>();
        foreach (var job in due)
        {
            var scheduled = job.NextRunAt!.Value;
            var next = job.ParsedSchedule.Following(scheduled, now);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE jobs SET next_run_at = $next, state = $state, last_run_at = $now WHERE job_id = $id;";
                update.Parameters.AddWithValue("$next", next is { } value ? value.UtcTicks : DBNull.Value);
                update.Parameters.AddWithValue("$state", StateName(next is null ? JobState.Completed : JobState.Active));
                update.Parameters.AddWithValue("$now", now.UtcTicks);
                update.Parameters.AddWithValue("$id", job.JobId);
                update.ExecuteNonQuery();
            }

            var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
            var run = new JobRun(runId, job.JobId, $"job:{job.JobId}:{runId}", scheduled, now, null, JobRunStatus.Running, null, null, null, null);
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO job_runs (run_id, job_id, session_id, scheduled_for, started_at, status)
                    VALUES ($run, $job, $session, $scheduled, $started, $status);
                    """;
                insert.Parameters.AddWithValue("$run", run.RunId);
                insert.Parameters.AddWithValue("$job", run.JobId);
                insert.Parameters.AddWithValue("$session", run.SessionId);
                insert.Parameters.AddWithValue("$scheduled", scheduled.UtcTicks);
                insert.Parameters.AddWithValue("$started", now.UtcTicks);
                insert.Parameters.AddWithValue("$status", run.Status);
                insert.ExecuteNonQuery();
            }

            claimed.Add(new ClaimedRun(job with
            {
                NextRunAt = next,
                State = next is null ? JobState.Completed : JobState.Active,
                LastRunAt = now,
            }, run));
        }

        transaction.Commit();
        return claimed;
    }

    /// <summary>Records how a running run ended.</summary>
    public JobRun Finish(string runId, string status, DateTimeOffset finishedAt, string? wakeDecisionId = null, int? scriptExitCode = null,
        string? output = null, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE job_runs SET status = $status, finished_at = $finished, wake_decision_id = $wake, script_exit_code = $exit,
                                output = $output, detail = $detail
            WHERE run_id = $run AND status = 'running';
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finished", finishedAt.UtcTicks);
        command.Parameters.AddWithValue("$wake", (object?)wakeDecisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$exit", (object?)scriptExitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$output", (object?)output ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", runId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"Run {runId} is not running.");
        return GetRun(runId)!;
    }

    public JobRun? GetRun(string runId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RunColumns} FROM job_runs WHERE run_id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRun(reader) : null;
    }

    /// <summary>A job's runs, newest first.</summary>
    public IReadOnlyList<JobRun> Runs(string jobId, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RunColumns} FROM job_runs WHERE job_id = $job ORDER BY started_at DESC, run_id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<JobRun>();
        while (reader.Read())
            runs.Add(ReadRun(reader));
        return runs;
    }

    /// <summary>At startup: runs left running by a process that stopped are marked interrupted. Returns how many.</summary>
    public int InterruptStale()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_runs SET status = 'interrupted', finished_at = $now, detail = 'Corvid stopped during the run' WHERE status = 'running';";
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().UtcTicks);
        return command.ExecuteNonQuery();
    }

    /// <summary>The time zone named by a job, falling back to UTC when this machine does not know it.</summary>
    public static TimeZoneInfo FindZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private const string JobColumns =
        "job_id, name, prompt, schedule, spec, time_zone, script, no_agent, origin_session, created_tainted, created_at, next_run_at, state, last_run_at, paused";

    private const string RunColumns =
        "run_id, job_id, session_id, scheduled_for, started_at, finished_at, status, wake_decision_id, script_exit_code, output, detail";

    private static ScheduledJob ReadJob(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetInt64(7) != 0, r.IsDBNull(8) ? null : r.GetString(8), r.GetInt64(9) != 0,
        Time(r.GetInt64(10)), r.IsDBNull(11) ? null : Time(r.GetInt64(11)), ParseState(r.GetString(12)),
        r.IsDBNull(13) ? null : Time(r.GetInt64(13)))
    {
        Paused = r.GetInt64(14) != 0,
    };

    private static JobRun ReadRun(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Time(r.GetInt64(3)), Time(r.GetInt64(4)), r.IsDBNull(5) ? null : Time(r.GetInt64(5)),
        r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10));

    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);

    private static string StateName(JobState state) => state switch
    {
        JobState.Active => "active",
        JobState.Completed => "completed",
        _ => "removed",
    };

    private static JobState ParseState(string state) => state switch
    {
        "active" => JobState.Active,
        "completed" => JobState.Completed,
        "removed" => JobState.Removed,
        _ => throw new InvalidDataException($"Unknown job state '{state}'."),
    };

    private const string Schema = """
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
            last_run_at INTEGER,
            paused INTEGER NOT NULL DEFAULT 0 CHECK (paused IN (0, 1))
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

        PRAGMA user_version = 2;
        """;

    private const string MigrateToVersion2 = """
        ALTER TABLE jobs ADD COLUMN paused INTEGER NOT NULL DEFAULT 0 CHECK (paused IN (0, 1));
        PRAGMA user_version = 2;
        """;
}
