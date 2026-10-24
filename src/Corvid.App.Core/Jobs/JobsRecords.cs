namespace Corvid.App.Core.Jobs;

/// <summary>A job's state, as <c>/cron</c> lists it: active, paused (active but skipped until resumed), or removed.</summary>
public enum JobState
{
    Active,
    Paused,
    /// <summary>Its schedule has no run left (a one-off that ran).</summary>
    Finished,
    Removed,
}

/// <summary>Where the scheduler runs (docs/plan/ui.md §4.10, F23): in this process, in another one that holds the lock, or nowhere.</summary>
public enum SchedulerHost
{
    Here,
    Elsewhere,
    Off,
}

/// <summary>
/// The scheduler's state: who holds its lock and since when. <paramref name="Holder"/> names the other process when it is
/// <see cref="SchedulerHost.Elsewhere"/> ("corvid serve, pid 4127").
/// </summary>
public sealed record SchedulerStatus(SchedulerHost Host, string Holder = "", DateTimeOffset? Since = null);

/// <summary>How a run ended, as the job store records it.</summary>
public enum JobRunStatus
{
    Ok,
    Failed,
    /// <summary>Not run: the job was paused, or the scheduler was off at its time and the run was too late to catch up.</summary>
    Skipped,
    Running,
}

/// <summary>One run of a job (C-041): when it was due, when it started and ended, how it went, and the session it ran in.</summary>
public sealed record JobRunRecord(string RunId, string JobId, DateTimeOffset ScheduledFor, DateTimeOffset StartedAt, JobRunStatus Status)
{
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>One line on what it did or why it failed; text from the run, so the page cleans it.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The session the run's turn ran in; empty for a no-agent run.</summary>
    public string SessionId { get; init; } = "";

    public int ToolCalls { get; init; }
}

/// <summary>
/// A scheduled job (C-012): its name and schedule as written, the schedule as the scheduler reads it, the prompt (or, for a
/// no-agent job, only its script), where the result goes, and its next and last runs. The name, prompt and script may have
/// come from the model through <c>cron_create</c>, so the page draws them cleaned.
/// </summary>
public sealed record JobRecord(string Id, string Name, string Schedule, JobState State)
{
    /// <summary>The schedule as the scheduler reads it ("cron 0 9 * * 1-5").</summary>
    public string Parsed { get; init; } = "";

    /// <summary>The zone its times are in ("Europe/Berlin", or the machine's).</summary>
    public string Zone { get; init; } = "";

    public string Prompt { get; init; } = "";

    /// <summary>The pre-run script, whose output the prompt sees; for a no-agent job, all that runs.</summary>
    public string? Script { get; init; }

    public bool NoAgent { get; init; }

    /// <summary>Created from a tainted session: its runs start tainted.</summary>
    public bool CreatedTainted { get; init; }

    /// <summary>Where the result is delivered ("the Telegram home chat", "this app").</summary>
    public string Deliver { get; init; } = "";

    /// <summary>Who created it ("you, in this app", "cli-9f2e through cron_create").</summary>
    public string CreatedBy { get; init; } = "";

    public DateTimeOffset? NextRun { get; init; }

    public JobRunRecord? LastRun { get; init; }
}

/// <summary>What the create form sends (G-13): the schedule in the grammar, and the rest as typed.</summary>
public sealed record JobDraft(string Name, string Schedule, string Prompt, string? Script, bool NoAgent, string Deliver);

/// <summary>
/// The schedule grammar's reading of a text ("in 30m", "weekdays at 9am", cron, ISO): what it means and the next runs, or
/// why it cannot be read.
/// </summary>
public sealed record SchedulePreview(bool Valid, string Parsed, IReadOnlyList<DateTimeOffset> Next, string Error = "")
{
    public static SchedulePreview Invalid(string error) => new(false, "", [], error);
}

/// <summary>Everything the Jobs page shows at once: the scheduler, the jobs, and where a result can be delivered.</summary>
public sealed record JobsSnapshot(SchedulerStatus Scheduler, IReadOnlyList<JobRecord> Jobs, IReadOnlyList<string> Destinations);
