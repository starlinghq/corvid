using System.Globalization;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Jobs;

/// <summary>
/// The Jobs area's demo, consistent with the rest of the demo at 2026-10-24 14:05: the scheduler runs here with two jobs
/// (the status bar's "here · 2 jobs"). <c>nightly-tidy</c> runs every day at 09:00; this morning's run is the session
/// <see cref="DemoSessionService.JobSessionId"/> (41 s, three tool calls, the result sent to the Telegram home chat, as the
/// Board's tray says), and its next run is tomorrow at 09:00. <c>weekly-deps</c>, made by <c>cron_create</c> in a tainted
/// session, is paused. Creating, pausing, resuming and removing change this state as the job store would; nothing is
/// written and nothing runs.
/// </summary>
public sealed class DemoJobsService : IJobsService
{
    public const string TidyId = "j-3f2a";
    public const string DepsId = "j-8c41";

    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly List<JobRecord> _jobs = [];
    private readonly Dictionary<string, List<JobRunRecord>> _runs = new(StringComparer.Ordinal);
    private readonly SchedulerStatus _scheduler;
    private int _next = 1;

    public DemoJobsService(TimeProvider time, SchedulerHost host = SchedulerHost.Here)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _zone = time.LocalTimeZone;
        _scheduler = host switch
        {
            SchedulerHost.Here => new SchedulerStatus(host, "this app", At(0, 8, 2)),
            SchedulerHost.Elsewhere => new SchedulerStatus(host, "corvid serve, pid 4127", At(0, 8, 0)),
            _ => new SchedulerStatus(host),
        };
        Seed();
    }

    public event EventHandler? Changed;

    public Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JobRecord> jobs = [.. _jobs.Where(job => job.State != JobState.Removed)];
        return Task.FromResult(new JobsSnapshot(_scheduler, jobs,
            ["this app", "the Telegram home chat", "Discord #corvid-lab", "nowhere (kept in the run's session)"]));
    }

    public Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JobRunRecord> runs = _runs.TryGetValue(jobId, out var list)
            ? [.. list.OrderByDescending(run => run.ScheduledFor).Take(JobsText.RunsShown)]
            : [];
        return Task.FromResult(runs);
    }

    public Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ScheduleGrammar.Read(schedule, _time.GetUtcNow(), _zone));
    }

    public Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        var name = draft.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("A job needs a name.");
        if (_jobs.Any(job => job.State != JobState.Removed && string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"There is already a job named {name}.");
        if (new[] { draft.Name, draft.Schedule, draft.Prompt, draft.Script }.Any(SafeText.HasHidden))
            throw new InvalidOperationException("The job holds hidden characters; nothing was created.");
        var preview = ScheduleGrammar.Read(draft.Schedule, _time.GetUtcNow(), _zone);
        if (!preview.Valid)
            throw new InvalidOperationException(preview.Error);
        if (draft.NoAgent && string.IsNullOrWhiteSpace(draft.Script))
            throw new InvalidOperationException("A no-agent job runs only its script; give it one.");
        if (!draft.NoAgent && string.IsNullOrWhiteSpace(draft.Prompt))
            throw new InvalidOperationException("Say what the job asks Corvid to do.");

        var job = new JobRecord($"j-{_next++:x4}", name, draft.Schedule.Trim(), JobState.Active)
        {
            Parsed = preview.Parsed,
            Zone = "local time",
            Prompt = draft.NoAgent ? "" : draft.Prompt.Trim(),
            Script = string.IsNullOrWhiteSpace(draft.Script) ? null : draft.Script.Trim(),
            NoAgent = draft.NoAgent,
            Deliver = draft.Deliver,
            CreatedBy = "you, in this app, today " + Local(_time.GetUtcNow()).ToString("HH:mm", CultureInfo.InvariantCulture),
            NextRun = preview.Next[0],
        };
        _jobs.Add(job);
        _runs[job.Id] = [];
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(job);
    }

    public Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = Find(jobId);
        if (job.State != JobState.Active)
            throw new InvalidOperationException($"{job.Name} is not an active, unpaused job.");
        return Task.FromResult(Replace(job with { State = JobState.Paused, NextRun = null }));
    }

    public Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = Find(jobId);
        if (job.State != JobState.Paused)
            throw new InvalidOperationException($"{job.Name} is not paused.");
        var next = Next(job.Parsed);
        return Task.FromResult(Replace(job with { State = next is null ? JobState.Finished : JobState.Active, NextRun = next }));
    }

    public Task RemoveAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = Find(jobId);
        if (job.State == JobState.Removed)
            throw new InvalidOperationException($"{job.Name} was already removed.");
        Replace(job with { State = JobState.Removed, NextRun = null });
        return Task.CompletedTask;
    }

    private JobRecord Find(string jobId) =>
        _jobs.Find(job => job.Id == jobId) ?? throw new InvalidOperationException($"There is no job {jobId}.");

    private JobRecord Replace(JobRecord job)
    {
        var index = _jobs.FindIndex(candidate => candidate.Id == job.Id);
        _jobs[index] = job;
        Changed?.Invoke(this, EventArgs.Empty);
        return job;
    }

    /// <summary>The next run of a schedule as the scheduler reads it; a one-off has none once it is due.</summary>
    private DateTimeOffset? Next(string parsed)
    {
        if (!parsed.StartsWith("cron ", StringComparison.Ordinal))
            return null;
        var preview = ScheduleGrammar.Cron(parsed[5..], _time.GetUtcNow(), _zone, 1);
        return preview.Valid ? preview.Next[0] : null;
    }

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone);

    /// <summary>A wall-clock time <paramref name="days"/> from the demo's day, in the machine's zone.</summary>
    private DateTimeOffset At(int days, int hour, int minute, int second = 0)
    {
        var day = Local(_time.GetUtcNow()).Date.AddDays(days);
        var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, _zone.GetUtcOffset(wall));
    }

    private void Seed()
    {
        var tidyRuns = new List<JobRunRecord>
        {
            Run(TidyId, "r-0412", 0, 9, 41, JobRunStatus.Ok, 3, DemoSessionService.JobSessionId,
                "3 merged branches, 12 TODOs older than a month, 2 files over 1 MB; 2 facts staged"),
            Run(TidyId, "r-0398", -1, 9, 38, JobRunStatus.Ok, 3, "job:nightly-tidy:2026-10-23",
                "2 merged branches, 11 old TODOs; nothing staged"),
            Run(TidyId, "r-0385", -2, 9, 44, JobRunStatus.Ok, 3, "job:nightly-tidy:2026-10-22", "1 merged branch, 11 old TODOs"),
            Run(TidyId, "r-0371", -3, 9, 120, JobRunStatus.Failed, 2, "job:nightly-tidy:2026-10-21",
                "the generator timed out at step 3 of 4 (qwen3.5-0.8b, 120 s); delivered nothing"),
            Run(TidyId, "r-0360", -4, 9, 36, JobRunStatus.Ok, 3, "job:nightly-tidy:2026-10-20", "nothing to tidy"),
        };
        _runs[TidyId] = tidyRuns;
        _jobs.Add(new JobRecord(TidyId, "nightly-tidy", "every day at 9am", JobState.Active)
        {
            Parsed = "cron 0 9 * * *",
            Zone = "local time",
            Prompt = "Tidy the workspace: list merged branches, TODOs older than a month and files over 1 MB; stage what is worth remembering.",
            Deliver = "the Telegram home chat",
            CreatedBy = "you, in cli-9f2e, on 2026-10-12",
            NextRun = At(1, 9, 0),
            LastRun = tidyRuns[0],
        });

        var depsRuns = new List<JobRunRecord>
        {
            Run(DepsId, "r-0322", -8, 16, 52, JobRunStatus.Ok, 1, "job:weekly-deps:2026-10-16",
                "4 packages behind, none with an advisory"),
            Run(DepsId, "r-0270", -15, 16, 49, JobRunStatus.Ok, 1, "job:weekly-deps:2026-10-09", "2 packages behind"),
        };
        _runs[DepsId] = depsRuns;
        _jobs.Add(new JobRecord(DepsId, "weekly-deps", "fridays at 4pm", JobState.Paused)
        {
            Parsed = "cron 0 16 * * 5",
            Zone = "local time",
            Prompt = "Summarise the outdated packages the script lists, newest advisories first. Change nothing.",
            Script = "scripts/outdated.sh",
            CreatedTainted = true,
            Deliver = "this app",
            CreatedBy = "cli-77b0 through cron_create, approved once, on 2026-10-02",
            LastRun = depsRuns[0],
        });
    }

    private JobRunRecord Run(string job, string id, int days, int hour, int seconds, JobRunStatus status, int calls, string session, string detail)
    {
        var due = At(days, hour, 0);
        return new JobRunRecord(id, job, due, due, status)
        {
            FinishedAt = due.AddSeconds(seconds),
            ToolCalls = calls,
            SessionId = session,
            Detail = detail,
        };
    }
}
