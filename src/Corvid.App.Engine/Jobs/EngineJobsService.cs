using System.Globalization;
using Corvid.App.Core;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Ops;
using Corvid.Gateway;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
using CoreJobState = Corvid.App.Core.Jobs.JobState;
using CoreRunStatus = Corvid.App.Core.Jobs.JobRunStatus;
using KernelJobState = Corvid.Kernel.Jobs.JobState;
using KernelRunStatus = Corvid.Kernel.Jobs.JobRunStatus;

namespace Corvid.App.Engine.Jobs;

/// <summary>
/// The Jobs page over the runtime's durable scheduler (C-012, C-041): the job store's jobs and runs, the schedule grammar's
/// preview through the kernel's own parser (G-13's <see cref="JobSchedule.Upcoming"/>, so the page previews exactly what
/// the store will run), create through <see cref="JobStore.Add"/>, and pause, resume and remove as <c>/cron</c> does them
/// (the person acting directly, so no approval step). The scheduler's state is the runtime's (<see cref="CorvidRuntime.SchedulerState"/>:
/// this app runs it when it holds <c>jobs.db.lock</c>, U-8).
/// <para>
/// <see cref="Changed"/> follows the store (<see cref="JobStore.Changed"/>, in this process: a <c>cron_create</c> in a
/// session, a <c>/cron</c>) and the scheduler (<see cref="CorvidRuntime.JobCompleted"/>), posted to the UI thread and
/// coalesced. A scheduler in another process is seen at the next load. Store calls are synchronous SQLite, so they run on
/// the pool, never on the UI thread.
/// </para>
/// <para>
/// G-14: the scheduler's status says since when, and who holds it when another process does (the lock's note); a load
/// that finds that process gone takes the scheduler over (<see cref="CorvidRuntime.WakeScheduler"/>), and while another
/// process runs it the store is looked at every <see cref="ElsewhereInterval"/>, so its runs show without a reload.
/// </para>
/// <para>
/// Job runs report back: a job made here keeps its result in the run's session (<see cref="KeptInSession"/>), or reports to
/// an enabled adapter's home chat (<c>Corvid:Gateway:Adapters:&lt;name&gt;:HomeChat</c>) through the delivery ledger, which
/// whichever gateway runs (this app's, G-14) sends. A job records no delivery target of its own, so such a job is made as a
/// gateway chat's job is (its origin is that chat's session). Runs record no tool-call count.
/// </para>
/// </summary>
public sealed class EngineJobsService : IJobsService, IDisposable
{
    /// <summary>The one place a job made here delivers: its result stays in the run's session.</summary>
    public const string KeptInSession = "nowhere (kept in the run's session)";

    /// <summary>The most jobs a load lists; the page shows fewer and points at <c>/cron</c> for the rest.</summary>
    public const int MaxJobs = 200;

    /// <summary>The longest prompt a job made here may have: it starts every run.</summary>
    public const int MaxPromptLength = 16_000;

    /// <summary>The longest pre-run script a job made here may have.</summary>
    public const int MaxScriptLength = 16_000;

    private readonly AppEngine _engine;
    private readonly TimeProvider _time;
    private readonly ChangeSignal _changed;
    private readonly BackgroundWork _work = new();
    private readonly Lock _gate = new();
    private Action? _unsubscribe;
    private bool _disposed;
    private ITimer? _elsewhere;
    private string? _seen;
    private int _looking;

    /// <summary>How often the store is looked at while another process runs the scheduler (its runs raise nothing here).</summary>
    public static readonly TimeSpan ElsewhereInterval = TimeSpan.FromSeconds(10);

    public EngineJobsService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _time = time;
        _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
        _engine.Started += OnEngineStarted;
        _work.Track(SubscribeAsync());
    }

    /// <summary>A runtime that could not start at first is up (First run's downloads): hear it, and the page reads it again.</summary>
    private void OnEngineStarted(CorvidRuntime runtime)
    {
        _work.Track(SubscribeAsync());
        _changed.Raise();
    }

    public event EventHandler? Changed;

    /// <summary>Waits for the subscription to the runtime (tests).</summary>
    internal Task IdleAsync() => _work.IdleAsync();

    public async Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var store = runtime.Builtin.Jobs;
        var snapshot = await Task.Run(() =>
        {
            // The process that held the scheduler has exited: this app takes it over (C-041's retry, as a session open does).
            if (runtime.SchedulerState == ElsewhereState)
                runtime.WakeScheduler();
            var state = runtime.SchedulerState;
            var jobs = store.List(includeFinished: true)
                .Where(job => job.State != KernelJobState.Removed)
                .OrderByDescending(job => job.CreatedAt)
                .Take(MaxJobs)
                .Select(job => Record(job, store.Runs(job.JobId, 1) is [var last] ? last : null))
                .ToList();
            return new JobsSnapshot(Scheduler(state, runtime.SchedulerHolder), jobs, [KeptInSession, .. HomeChats(runtime.Options).Keys]);
        }, cancellationToken).ConfigureAwait(false);
        Watch(runtime, snapshot);
        return snapshot;
    }

    public async Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var store = runtime.Builtin.Jobs;
        return await Task.Run(() => (IReadOnlyList<JobRunRecord>)[.. store.Runs(jobId, JobsText.RunsShown).Select(Run)], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken)
    {
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Preview(schedule, _time.GetUtcNow(), runtime.Builtin.Jobs.TimeZone);
    }

    /// <summary>The kernel's reading of a schedule and its next runs, as the store would time a job created now.</summary>
    internal static SchedulePreview Preview(string? schedule, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return ScheduleGrammar.Read(schedule, now, zone);
        if (SafeText.HasHidden(schedule))
            return SchedulePreview.Invalid("The schedule holds hidden characters; type it again.");
        try
        {
            var parsed = JobSchedule.Parse(schedule, now, zone);
            var next = parsed.Upcoming(now, ScheduleGrammar.Shown);
            return next.Count == 0
                ? SchedulePreview.Invalid($"'{schedule.Trim()}' never runs.")
                : new SchedulePreview(true, Readable(parsed.Canonical), next);
        }
        catch (FormatException exception)
        {
            return SchedulePreview.Invalid(exception.Message);
        }
    }

    public async Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var store = runtime.Builtin.Jobs;
        var name = draft.Name.Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("A job needs a name.");
        if (new[] { draft.Name, draft.Schedule, draft.Prompt, draft.Script, draft.Deliver }.Any(SafeText.HasHidden))
            throw new InvalidOperationException("The job holds hidden characters; nothing was created.");
        if (draft.NoAgent && string.IsNullOrWhiteSpace(draft.Script))
            throw new InvalidOperationException("A no-agent job runs only its script; give it one.");
        if (!draft.NoAgent && string.IsNullOrWhiteSpace(draft.Prompt))
            throw new InvalidOperationException("Say what the job asks Corvid to do.");
        if (draft.Prompt.Length > MaxPromptLength)
            throw new InvalidOperationException(
                $"The prompt is {draft.Prompt.Length} characters; a job's prompt may be at most {MaxPromptLength}. Put the detail in a file the job reads.");
        if ((draft.Script?.Length ?? 0) > MaxScriptLength)
            throw new InvalidOperationException(
                $"The script is {draft.Script!.Length} characters; a job's script may be at most {MaxScriptLength}. Save it as a file and run that.");
        string? origin = null;
        if (draft.Deliver.Length > 0 && draft.Deliver != KeptInSession && !HomeChats(runtime.Options).TryGetValue(draft.Deliver, out origin))
            throw new InvalidOperationException(
                $"A job made here keeps its result in the run's session or reports to an enabled adapter's home chat (Corvid:Gateway:Adapters:<name>:HomeChat); {SafeText.Reveal(draft.Deliver)} is neither.");

        var created = await Task.Run(() =>
        {
            // A check, not a constraint: two creations racing here could both pass and make two jobs of one name. Names are
            // labels (jobs are addressed by id), so that is untidy rather than wrong.
            if (store.List().Any(job => string.Equals(job.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"There is already a job named {name}.");
            try
            {
                // A job reporting to a chat is made as that chat's job is: the run's result is queued for it in the delivery ledger.
                return store.Add(new NewJob(name, draft.NoAgent ? "" : draft.Prompt, draft.Schedule,
                    string.IsNullOrWhiteSpace(draft.Script) ? null : draft.Script.Trim(), draft.NoAgent, OriginSession: origin));
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return Record(created, null);
    }

    public async Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var store = runtime.Builtin.Jobs;
        var job = await Task.Run(() =>
        {
            var current = Find(store, jobId);
            if (current.State != KernelJobState.Active || current.Paused || !store.Pause(jobId))
                throw new InvalidOperationException($"{SafeText.Clean(current.Name)} is not an active, unpaused job.");
            return Latest(store, jobId);
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return job;
    }

    public async Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var store = runtime.Builtin.Jobs;
        var job = await Task.Run(() =>
        {
            var current = Find(store, jobId);
            if (current.State != KernelJobState.Active || !current.Paused)
                throw new InvalidOperationException($"{SafeText.Clean(current.Name)} is not paused.");
            // A job cron_create made from a chat was never checked by this page: one whose text hides characters is not
            // run again from here until it is seen (A2's rule, which the demo keeps too).
            if (JobsText.HiddenPart(Record(current, null)) is { } part)
                throw new InvalidOperationException($"{SafeText.Clean(current.Name)}'s {part} holds hidden characters; it cannot be resumed.");
            if (store.Resume(jobId) is null)
                throw new InvalidOperationException($"{SafeText.Clean(current.Name)} is not paused.");
            return Latest(store, jobId);
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
        return job;
    }

    public async Task RemoveAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var store = runtime.Builtin.Jobs;
        await Task.Run(() =>
        {
            var current = Find(store, jobId);
            if (current.State == KernelJobState.Removed)
                throw new InvalidOperationException($"{SafeText.Clean(current.Name)} was already removed.");
            if (!store.Remove(jobId))
                throw new InvalidOperationException($"{SafeText.Clean(current.Name)} has finished; there is nothing left to remove.");
        }, cancellationToken).ConfigureAwait(false);
        _changed.Raise();
    }

    public void Dispose()
    {
        _engine.Started -= OnEngineStarted;
        Action? unsubscribe;
        ITimer? elsewhere;
        lock (_gate)
        {
            _disposed = true;
            (unsubscribe, _unsubscribe) = (_unsubscribe, null);
            (elsewhere, _elsewhere) = (_elsewhere, null);
        }
        unsubscribe?.Invoke();
        elsewhere?.Dispose();
        _changed.Dispose();
    }

    /// <summary>
    /// The destinations a job made here may report to besides its session: each enabled adapter's home chat, by the label the
    /// form shows ("the telegram home chat (-100123)"), to the gateway session a chat's own job has as its origin.
    /// </summary>
    internal static Dictionary<string, string> HomeChats(Corvid.Host.Configuration.CorvidOptions options) =>
        options.Gateway.Adapters
            .Where(pair => pair.Value is { Enabled: true, HomeChat.Length: > 0 } && !pair.Key.Contains(':', StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => $"the {pair.Key.ToLowerInvariant()} home chat ({pair.Value.HomeChat})",
                pair => GatewaySessions.For(pair.Key.ToLowerInvariant(), pair.Value.HomeChat!), StringComparer.Ordinal);

    /// <summary>
    /// While another process runs the scheduler, looks at the store every <see cref="ElsewhereInterval"/> and raises
    /// <see cref="Changed"/> when its jobs, their latest runs or the scheduler's holder differ, taking the scheduler over
    /// once that process has exited; stops once this app runs it or it is off.
    /// </summary>
    private void Watch(CorvidRuntime runtime, JobsSnapshot snapshot)
    {
        var elsewhere = snapshot.Scheduler.Host == SchedulerHost.Elsewhere;
        ITimer? stop = null;
        lock (_gate)
        {
            if (_disposed)
                return;
            _seen = Fingerprint(snapshot.Jobs) + "\n" + snapshot.Scheduler;
            if (elsewhere && _elsewhere is null)
                _elsewhere = _time.CreateTimer(_ => Look(runtime), null, ElsewhereInterval, ElsewhereInterval);
            else if (!elsewhere)
                (stop, _elsewhere) = (_elsewhere, null);
        }
        stop?.Dispose();
    }

    private void Look(CorvidRuntime runtime)
    {
        if (Interlocked.Exchange(ref _looking, 1) != 0)
            return;
        _work.Track(Task.Run(() =>
        {
            try
            {
                var store = runtime.Builtin.Jobs;
                // The holder exited: take the scheduler over now, as a load would, rather than at the next session open.
                if (runtime.SchedulerState == ElsewhereState)
                    runtime.WakeScheduler();
                var now = Fingerprint([.. store.List(includeFinished: true).Where(job => job.State != KernelJobState.Removed)
                    .OrderByDescending(job => job.CreatedAt).Take(MaxJobs).Select(job => Record(job, store.Runs(job.JobId, 1) is [var last] ? last : null))])
                    + "\n" + Scheduler(runtime.SchedulerState, runtime.SchedulerHolder);
                var changed = false;
                lock (_gate)
                {
                    if (!_disposed && now != _seen)
                        (_seen, changed) = (now, true);
                }
                if (changed)
                    _changed.Raise();
            }
            finally
            {
                Volatile.Write(ref _looking, 0);
            }
        }));
    }

    /// <summary>What a reload would show differently: each job's state and next run, and its latest run's status.</summary>
    private static string Fingerprint(IReadOnlyList<JobRecord> jobs) =>
        string.Join('\n', jobs.Select(job => string.Join('|', job.Id, job.State, job.NextRun?.UtcTicks, job.LastRun?.RunId, job.LastRun?.Status,
            job.LastRun?.FinishedAt?.UtcTicks)));

    /// <summary>Once the runtime is up: hear the store's changes and the scheduler's runs until disposed.</summary>
    private async Task SubscribeAsync()
    {
        CorvidRuntime runtime;
        try
        {
            runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        }
        catch (Corvid.App.Core.Composition.ServiceUnavailableException)
        {
            // The page says why when it loads.
            return;
        }
        var store = runtime.Builtin.Jobs;
        void OnStoreChanged() => _changed.Raise();
        void OnCompleted(ScheduledJob job, JobRun run) => _changed.Raise();
        lock (_gate)
        {
            // Disposed, or subscribed already (a second start never reaches here with the first runtime up).
            if (_disposed || _unsubscribe is not null)
                return;
            store.Changed += OnStoreChanged;
            runtime.JobCompleted += OnCompleted;
            _unsubscribe = () =>
            {
                store.Changed -= OnStoreChanged;
                runtime.JobCompleted -= OnCompleted;
            };
        }
    }

    /// <summary>The runtime's state while another process holds the scheduler's lock.</summary>
    private const string ElsewhereState = "run by another corvid process";

    private static ScheduledJob Find(JobStore store, string jobId) =>
        store.Get(jobId) ?? throw new InvalidOperationException($"There is no job {SafeText.Reveal(jobId)}.");

    private static JobRecord Latest(JobStore store, string jobId) =>
        Record(Find(store, jobId), store.Runs(jobId, 1) is [var last] ? last : null);

    /// <summary>
    /// The runtime's scheduler state as the page's three: running in this process, held by another (whose lock keeps it),
    /// or off here (disabled in the configuration, or the lock file could not be written), with the reason.
    /// </summary>
    internal static SchedulerStatus Scheduler(string state, LockHolder? holder = null) => state switch
    {
        "running in this process" => new SchedulerStatus(SchedulerHost.Here, "this app", holder?.Since),
        ElsewhereState => new SchedulerStatus(SchedulerHost.Elsewhere, (holder?.ToString() ?? "another corvid process") + " (it holds jobs.db.lock)",
            holder?.Since),
        _ when state.StartsWith("off: ", StringComparison.Ordinal) =>
            new SchedulerStatus(SchedulerHost.Off, "This app cannot run the scheduler: " + state["off: ".Length..]),
        _ => new SchedulerStatus(SchedulerHost.Off, "This app does not run the scheduler: Corvid:Scheduler:Enabled is off"),
    };

    internal static JobRecord Record(ScheduledJob job, JobRun? last) => new(job.JobId, job.Name, job.Schedule, State(job))
    {
        Parsed = Readable(job.Spec),
        Zone = job.TimeZoneId,
        Prompt = job.Prompt,
        Script = job.Script,
        NoAgent = job.NoAgent,
        CreatedTainted = job.CreatedTainted,
        Deliver = GatewaySessions.TryParse(job.OriginSession, out var adapter, out var chat) ? $"the {adapter} chat {chat}" : KeptInSession,
        CreatedBy = (job.OriginSession is { Length: > 0 } origin
                ? GatewaySessions.TryParse(origin, out _, out _) ? origin + " (through cron_create there, or here to report there)" : origin + " through cron_create"
                : "outside a session (this app or /cron)")
            + ", on " + job.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        NextRun = job.NextRunAt,
        LastRun = last is null ? null : Run(last),
    };

    /// <summary>
    /// The store's canonical schedule as the page shows it: a one-shot's instant to the minute in UTC
    /// (<c>once 2026-10-24 21:39 UTC</c>), an interval in its largest whole unit (<c>every 2h</c>), a cron line as it is.
    /// </summary>
    internal static string Readable(string canonical)
    {
        if (canonical.StartsWith("once ", StringComparison.Ordinal)
            && DateTimeOffset.TryParseExact(canonical[5..], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return "once " + at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        if (canonical.StartsWith("every ", StringComparison.Ordinal) && canonical.EndsWith('s')
            && long.TryParse(canonical[6..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return "every " + (seconds % 86_400 == 0 ? $"{seconds / 86_400}d" : seconds % 3_600 == 0 ? $"{seconds / 3_600}h"
                : seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds}s");
        return canonical;
    }

    private static CoreJobState State(ScheduledJob job) => job.State switch
    {
        KernelJobState.Removed => CoreJobState.Removed,
        KernelJobState.Completed => CoreJobState.Finished,
        _ => job.Paused ? CoreJobState.Paused : CoreJobState.Active,
    };

    internal static JobRunRecord Run(JobRun run) => new(run.RunId, run.JobId, run.ScheduledFor, run.StartedAt, Status(run.Status))
    {
        FinishedAt = run.FinishedAt,
        Detail = Detail(run),
        // A no-agent run's session holds only its script's approval; it is still where the run is recorded.
        SessionId = run.SessionId,
    };

    private static CoreRunStatus Status(string status) => status switch
    {
        KernelRunStatus.Running => CoreRunStatus.Running,
        KernelRunStatus.Ran or KernelRunStatus.ScriptOnly => CoreRunStatus.Ok,
        KernelRunStatus.Skipped => CoreRunStatus.Skipped,
        _ => CoreRunStatus.Failed,
    };

    /// <summary>One line on the run: its stored status where it says more than ok/failed, then its detail or the start of its output.</summary>
    private static string Detail(JobRun run)
    {
        // A run that answered says so in its detail ("answered"); its answer says what it did.
        var text = run.Detail is { } said && run.Output is { Length: > 0 } output ? said + ": " + output : run.Detail ?? run.Output ?? "";
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length > 160)
        {
            var cut = char.IsHighSurrogate(line[158]) ? 158 : 159;
            line = line[..cut].TrimEnd() + "…";
        }
        var status = run.Status switch
        {
            KernelRunStatus.ScriptRefused => "script refused",
            KernelRunStatus.Interrupted => "interrupted",
            KernelRunStatus.ScriptOnly => "script only",
            _ => null,
        };
        return status is null ? line : line.Length == 0 ? status : $"{status}: {line}";
    }
}
