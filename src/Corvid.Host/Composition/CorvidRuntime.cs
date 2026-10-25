using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.IO;
using Corvid.Abstractions.Sessions;
using Corvid.Abstractions.Tokens;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Gateway;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Host.State;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Context;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Mcp;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Skills;
using Corvid.Tools;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Composition;

/// <summary>The memory store's parts (plan §9), shared by every session.</summary>
public sealed record MemoryServices(
    MemoryDatabase Database,
    CoreBlockStore Blocks,
    EpisodicIndex Episodes,
    MemoryVectors Vectors,
    HybridRetriever Retriever,
    MemoryWriter? Writer)
{
    /// <summary>The lessons tier's gated writer and store (C-025); null when <c>Corvid:Memory:Lessons:Enabled</c> is off.</summary>
    public LessonWriter? Lessons { get; init; }

    /// <summary>Drafts lessons from queued turns in the background (C-025); null without lessons or a background generator.</summary>
    public Kernel.Lessons.LessonLearner? LessonLearner { get; init; }
}

/// <summary>
/// One running agent: the event log, the slots, the fabric, approvals, memory, tools and the session host, composed from
/// configuration. Surfaces open sessions on it and post messages; every session shares the process-wide services and has its
/// own slots and turn runner.
/// </summary>
public sealed partial class CorvidRuntime : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CorvidSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Delivery>> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionSettings> _settings = new(StringComparer.Ordinal);
    // Job runs whose turn ended tainted, by run session, until QueueJobResult takes them. The scheduler and its completion
    // callback run in this process, so the job store needs no column for it.
    private readonly ConcurrentDictionary<string, byte> _taintedRuns = new(StringComparer.Ordinal);
    private readonly ModelComposer _composer;
    // A5: the decider per session, over the shared fabric.
    private readonly SessionDeciders _deciders;
    private readonly ApprovalRouter _prompters;
    private readonly EmbedderSwapJob? _swap;
    private readonly ToolShortlist _shortlist = new();
    private readonly ToolShortlist _skillShortlist = new(new ShortlistOptions(Size: 3));
    private readonly Lock _maintenanceGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    // Cancels the titles being written when a surface leaves before they are done (MaintenanceIdleAsync with a bound).
    private readonly CancellationTokenSource _titling = new();
    private Task _maintenance = Task.CompletedTask;
    private ProcessLock? _schedulerLock;
    private Task? _scheduler;
    // Stops this process's scheduler without stopping the runtime (the app's Stop hosting); linked to _lifetime.
    private CancellationTokenSource? _schedulerStop;
    private JobScheduler? _jobScheduler;
    // Starting the scheduler (C-041: again on a session open after losing the lock) and disposal's lock release, one at a time.
    private readonly Lock _schedulerGate = new();
    // JobCompleted's handlers and the runs completed so far, under one gate (C-039).
    private readonly ReplayingEvent<ScheduledJob, JobRun> _jobCompleted = new(JobCompletedReplay);
    private Task? _learning;
    private Task? _lessonLearning;

    internal CorvidRuntime(CorvidOptions options, ModelCatalogue catalogue, ModelComposer composer, DeviceSchedulers devices,
        ModelMetrics metrics, TurnFeed feed, SqliteEventLog log, ModelSlotRegistry registry, DecisionFabric fabric,
        ApprovalPipeline approvals, ApprovalRouter prompters, ToolRegistry tools, MemoryServices? memory, ITokenCounter? counter,
        BuiltinServices builtin, CheckpointService? checkpoints = null, SkillServices? skills = null, McpClients? mcp = null,
        bool hostScheduler = true)
    {
        Mcp = mcp;
        Checkpoints = checkpoints;
        Skills = skills ?? new SkillServices(null, null, null, null, "off");
        Options = options;
        Catalogue = catalogue;
        _composer = composer;
        // L10: the ledger reports from inside a model call; never onto the terminal, which a turn is drawing.
        composer.Ledger.Failed = Report;
        Devices = devices;
        Metrics = metrics;
        Feed = feed;
        Log = log;
        Registry = registry;
        Fabric = fabric;
        _deciders = new SessionDeciders(fabric, registry);
        Approvals = approvals;
        _prompters = prompters;
        Tools = tools;
        Memory = memory;
        Counter = counter;
        Builtin = builtin;
        _swap = memory is null ? null : new EmbedderSwapJob(memory.Vectors);
        Sessions = new SessionHost(CreateActor, new SessionHostOptions
        {
            // A message whose session cannot be routed (its driver lock went to another process after a release, M2) fails
            // its send with why, rather than leaving the sender waiting.
            RoutingFailed = (envelope, exception) =>
            {
                if (envelope.MessageId is { } id && _waiting.TryRemove(id, out var waiter))
                    waiter.TrySetException(exception);
            },
        });
        if (memory is not null && registry.TryGet(ModelSlots.TextEmbedder, out var embedder)
            && memory.Vectors.State.Active != embedder.Default.Profile.Id)
            // The configured embedder changed since the store was written (or the store is new): §4.3's swap, in the background.
            _swap!.Start(embedder.Default.Profile.Id);
        if (options.Scheduler.Enabled && hostScheduler)
            TryStartScheduler();
        else if (options.Scheduler.Enabled)
            SchedulerState = SchedulerNotHosted;
        // The learning loop (plan §11): queued opportunities are drafted and replay-tested in the background.
        if (Skills.Learner is { } learner)
            _learning = Task.Run(() => learner.RunAsync(_lifetime.Token));
        // C-025: lesson drafts, the same way.
        if (memory?.LessonLearner is { } lessons)
            _lessonLearning = Task.Run(() => lessons.RunAsync(_lifetime.Token));
    }

    /// <summary>
    /// The workspace's project (C-025), resolved once at start: a runtime has one workspace. Null when lessons are off.
    /// </summary>
    public ProjectIdentity? Project { get; init; }

    /// <summary>The configured MCP servers (C-018), starting or started in the background; null when none is configured.</summary>
    public McpClients? Mcp { get; }

    /// <summary>Skills (C-017): the store, the learning loop and the curator, or why they are off.</summary>
    public SkillServices Skills { get; }

    public CorvidOptions Options { get; }

    /// <summary>
    /// The settings a running runtime takes without a restart (A4), by configuration key, with when each applies: the
    /// default approval mode for sessions opened from now on, the approval deadline from the next prompt, the step budget
    /// and output tokens per step for sessions opened from now on. Everything else in corvid.json is read once, at start.
    /// </summary>
    public static IReadOnlyDictionary<string, LiveSetting> LiveSettings { get; } = new Dictionary<string, LiveSetting>(StringComparer.OrdinalIgnoreCase)
    {
        [$"{CorvidOptions.Section}:Approvals:Mode"] = LiveSetting.NewSessions,
        [$"{CorvidOptions.Section}:Approvals:TimeoutSeconds"] = LiveSetting.Now,
        [$"{CorvidOptions.Section}:Turn:MaxSteps"] = LiveSetting.NewSessions,
        [$"{CorvidOptions.Section}:Turn:MaxOutputTokens"] = LiveSetting.NewSessions,
    };

    /// <summary>
    /// Takes the <see cref="LiveSettings"/> from <paramref name="next"/> (the configuration as the next start would read it,
    /// after a surface wrote corvid.json) and returns the keys whose value changed here. A session already open keeps the
    /// approval mode and step budget it was opened with; the approval deadline applies to the next prompt of every session.
    /// </summary>
    public ImmutableArray<string> ApplyLiveSettings(CorvidOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var applied = ImmutableArray.CreateBuilder<string>();
        lock (Options)
        {
            if (next.Approvals.Mode != Options.Approvals.Mode)
            {
                Options.Approvals.Mode = next.Approvals.Mode;
                applied.Add($"{CorvidOptions.Section}:Approvals:Mode");
            }
            if (next.Approvals.TimeoutSeconds != Options.Approvals.TimeoutSeconds && next.Approvals.TimeoutSeconds > 0)
            {
                Options.Approvals.TimeoutSeconds = next.Approvals.TimeoutSeconds;
                Approvals.SetTimeout(TimeSpan.FromSeconds(next.Approvals.TimeoutSeconds));
                applied.Add($"{CorvidOptions.Section}:Approvals:TimeoutSeconds");
            }
            if (next.Turn.MaxSteps != Options.Turn.MaxSteps)
            {
                Options.Turn.MaxSteps = next.Turn.MaxSteps;
                applied.Add($"{CorvidOptions.Section}:Turn:MaxSteps");
            }
            if (next.Turn.MaxOutputTokens != Options.Turn.MaxOutputTokens)
            {
                Options.Turn.MaxOutputTokens = next.Turn.MaxOutputTokens;
                applied.Add($"{CorvidOptions.Section}:Turn:MaxOutputTokens");
            }
        }
        return applied.ToImmutable();
    }

    public ModelCatalogue Catalogue { get; }

    public DeviceSchedulers Devices { get; }

    public ModelMetrics Metrics { get; }

    public TurnFeed Feed { get; }

    public SqliteEventLog Log { get; }

    public ModelSlotRegistry Registry { get; }

    public DecisionFabric Fabric { get; }

    public ApprovalPipeline Approvals { get; }

    public ToolRegistry Tools { get; }

    /// <summary>Null when memory is disabled in configuration.</summary>
    public MemoryServices? Memory { get; }

    public SessionHost Sessions { get; }

    /// <summary>C-012's shared services: the protected approval environment, the sandboxed shell and the job store.</summary>
    public BuiltinServices Builtin { get; }

    /// <summary>
    /// What answers the fabric's questions: Kev (with its second tier for gates when configured), or the degraded tier and
    /// why. Shown by <c>/status</c>; a surface prints <see cref="DeciderNotices"/> when it starts.
    /// </summary>
    public string DeciderState => Fabric.CalibratedDecider is { } calibrated
        ? Fabric.EscalationDecider is { } escalation
            ? $"{calibrated} on {DeciderPlacement}, {escalation} for gates below their floor{ReducedNote}; degraded tier when Kev declines"
            : $"{calibrated} on {DeciderPlacement}{ReducedNote}; degraded tier when Kev declines"
        : $"degraded tier (no calibrated decider){ReducedNote}";

    // C-030: the checkpoint profile, shown only when reduced.
    private string ReducedNote => Fabric.Reduction is null ? "" : "; reduced checkpoints (C-030)";

    /// <summary>
    /// Where the calibrated decider actually runs (C-029): <c>cpu</c> or <c>cuda:N</c>, the effective
    /// <see cref="ModelProfile.Placement"/> of the decider slot's default binding, so a configured GPU that fell back
    /// reads <c>cpu</c>; null on the degraded tier. C-030 reads the same placement in <c>CorvidRuntimeFactory.ReductionFor</c>
    /// and reduces checkpoints under <c>Auto</c> only when it is exactly <c>cpu</c>, so CUDA and a remote decider stay full.
    /// </summary>
    public string? DeciderPlacement => Registry.TryGet(ModelSlots.Decider, out var decider) ? decider.Default.Profile.Placement : null;

    /// <summary>Why the decider is on the degraded tier or without its second tier: a configured Kev that is not downloaded or did not load.</summary>
    public IReadOnlyList<string> DeciderNotices => _composer.DeciderNotices;

    /// <summary>The workspace's checkpoints (C-014); null when <c>Corvid:Checkpoints:Enabled</c> is off.</summary>
    public CheckpointService? Checkpoints { get; }

    /// <summary>Escalation and fallback targets left out at start because their service is not there (a cloud model without Vault).</summary>
    public IReadOnlyList<ModelAvailability> UnavailableModels { get; init; } = [];

    /// <summary>The privacy egress gate (plan §10.4) every gated binding and every session's switches share.</summary>
    public PrivacyEgress Egress => _composer.Egress;

    /// <summary>The egress ledger (G-9): every request the gate decided on, with the request as sent kept beside the log.</summary>
    public Audit.EgressLedger EgressLedger => _composer.Ledger;

    /// <summary>
    /// Whether this process runs due jobs, and if not, why. "run by another corvid process" can become "running in this
    /// process" when a session opens after that process exited (C-041, <see cref="OpenSession"/>).
    /// </summary>
    public string SchedulerState { get; private set; } = "off (Corvid:Scheduler:Enabled)";

    // The state of a runtime that lost jobs.db.lock to another process; the only one a session open retries (C-041).
    private const string SchedulerElsewhere = "run by another corvid process";

    /// <summary>
    /// The state of a runtime that does not host the scheduler (the windowed app until it is asked to, U-8): it neither runs
    /// due jobs nor takes the scheduler over; <see cref="StartScheduler"/> makes it try.
    /// </summary>
    public const string SchedulerNotHosted = "off (not hosted by this process)";

    /// <summary>The scheduler's lock beside the job store: whichever process holds it runs the jobs (U-8).</summary>
    public string SchedulerLockPath => Builtin.Jobs.Path + ".lock";

    /// <summary>
    /// Who runs the scheduler and since when (G-14's status): this process since it took the lock, or the process whose
    /// note is beside the lock; null when nobody does, or the holder left no note.
    /// </summary>
    public LockHolder? SchedulerHolder
    {
        get
        {
            lock (_schedulerGate)
                if (_schedulerLock is { } held)
                    return held.Holder;
            return Options.Scheduler.Enabled && ProcessLock.Probe(SchedulerLockPath) is { State: LockState.HeldElsewhere } probe ? probe.Holder : null;
        }
    }

    /// <summary>
    /// Raised after each scheduled run is recorded; a surface can say so (delivery to chats is C-020's). The scheduler
    /// starts with the runtime, so a run can finish before a surface subscribes (C-039): a new handler is replayed the runs
    /// this runtime completed before it was added (the most recent <see cref="JobCompletedReplay"/>), synchronously on the
    /// subscribing thread, and hears each later run live on the scheduler's thread; a live run can arrive while the replay
    /// is still running, so handlers must be thread-safe. Each handler sees each run exactly once. A handler added long
    /// after start can therefore hear old runs first.
    /// </summary>
    public event Action<ScheduledJob, JobRun>? JobCompleted
    {
        add => _jobCompleted.Add(value);
        remove => _jobCompleted.Remove(value);
    }

    /// <summary>How many completed runs <see cref="JobCompleted"/> keeps to replay to a handler added later.</summary>
    public const int JobCompletedReplay = 32;

    /// <summary>
    /// Session titles and recaps (G-6): generated after a session's first exchange (K-9) on the local generator, renamed by
    /// <see cref="Rename"/>, recapped by <see cref="RecapAsync"/>. Without one set at start, nothing is generated.
    /// </summary>
    public SessionTitler Titles
    {
        get => LazyInitializer.EnsureInitialized(ref _titles, () => new SessionTitler(Log, null, unavailable: "titles were not set up"));
        init => _titles = value;
    }

    private SessionTitler? _titles;

    /// <summary><c>ui.db</c> (G-7): drafts, pins, review points, when sessions were last seen and the app's preferences.</summary>
    public UiStateStore UiState
    {
        get => LazyInitializer.EnsureInitialized(ref _uiState, () => new UiStateStore(UiStateStore.PathFor(Options)));
        init => _uiState = value;
    }

    private UiStateStore? _uiState;

    // Sessions known to hold a title, so a turn after the first does not read the log to find out again.
    private readonly ConcurrentDictionary<string, byte> _titled = new(StringComparer.Ordinal);

    /// <summary>
    /// A session's title changed (G-6): the local generator wrote one after the first exchange, or someone renamed it here.
    /// Raised on the thread that wrote it (a pool thread for a generated one); handlers must be thread-safe.
    /// </summary>
    public event Action<string, SessionTitled>? TitleChanged;

    /// <summary>A recap was written here (G-6). Raised on the thread that wrote it; handlers must be thread-safe.</summary>
    public event Action<string, SessionRecapped>? Recapped;

    /// <summary>
    /// Something in the background went wrong that a person should hear of without it stopping anything (the egress ledger
    /// could not record a request, or prune its store). Raised from any thread, often inside a model call: a surface shows it
    /// its own way (the CLI as a notice, the app in its log), never by writing to the terminal from the handler's thread
    /// outside its renderer. With nobody listening it is a trace line. The latest few are kept in <see cref="Notices"/>.
    /// </summary>
    public event Action<string>? Notice;

    /// <summary>The latest background notices (<see cref="Notice"/>), oldest first, at most 20.</summary>
    public IReadOnlyList<string> Notices
    {
        get
        {
            lock (_notices)
                return [.. _notices];
        }
    }

    private readonly Queue<string> _notices = new();

    private void Report(string reason)
    {
        var line = "egress ledger: " + reason;
        lock (_notices)
        {
            _notices.Enqueue(line);
            while (_notices.Count > 20)
                _notices.Dequeue();
        }
        if (Notice is { } notice)
            notice(line);
        else
            System.Diagnostics.Trace.WriteLine("corvid: " + line);
    }

    /// <summary>Renames a session (G-6): a <c>SessionTitled</c> by the user in the log, then <see cref="TitleChanged"/>.</summary>
    /// <exception cref="ArgumentException">The title is blank.</exception>
    /// <exception cref="SessionHeldException">Another process drives the session (G-12); nothing was written.</exception>
    public SessionTitled Rename(string sessionId, string title)
    {
        SessionTitled titled;
        using (var guard = GuardWrite(sessionId))
            titled = Titles.Rename(sessionId, title, guard.Check);
        _titled[sessionId] = 0;
        TitleChanged?.Invoke(sessionId, titled);
        return titled;
    }

    /// <summary>
    /// A recap of the session's events since its last one, written by the local generator and logged (G-6), then
    /// <see cref="Recapped"/>; null when nothing happened since.
    /// </summary>
    /// <exception cref="InvalidOperationException">No local generator, or it failed.</exception>
    /// <exception cref="SessionHeldException">
    /// Another process drives the session (G-12), checked before the recap is generated and again before it is written;
    /// nothing was written.
    /// </exception>
    public async Task<SessionRecapped?> RecapAsync(string sessionId, string source = RecapSources.Requested, CancellationToken cancellationToken = default)
    {
        SessionRecapped? recap;
        // The check before generating only looks (the generator takes seconds, and the lock would keep a terminal out);
        // the write takes the lock (L7).
        using (var guard = GuardWrite(sessionId, probes: 1))
            recap = await Titles.RecapAsync(sessionId, source, guard.Check, cancellationToken).ConfigureAwait(false);
        if (recap is not null)
            Recapped?.Invoke(sessionId, recap);
        return recap;
    }

    internal ITokenCounter? Counter { get; }

    /// <summary>
    /// A new session, or an earlier one by id: its transcript is in the log, so the next turn sees it. It also wakes the
    /// scheduler, or takes it over when the process that held it has exited (C-041, <c>WakeScheduler</c>).
    /// </summary>
    /// <param name="settings">How the session's turns run; applied when this process first opens the session.</param>
    /// <exception cref="SessionHeldException">Another process drives the session (G-12); nothing was opened.</exception>
    public CorvidSession OpenSession(string? sessionId = null, string surface = "cli", SessionSettings? settings = null)
    {
        var id = sessionId ?? NewSessionId(surface);
        // A5: a child session is opened only by its parent's run (OpenChildSession); opened again by id (an editor, the
        // HTTP API, a terminal) it would be an ordinary session with the full toolset. Every surface shows it read-only.
        RefuseChildSession(id);
        TakeDriver(id);
        if (settings is not null)
            _settings.TryAdd(id, settings);
        Sessions.GetOrCreate(id);
        _settings.TryRemove(id, out _);
        var session = _sessions[id];
        session.Surface = surface;
        WakeScheduler();
        return session;
    }

    /// <summary>
    /// Forks a session at a log position (plan §3.2): a new session holding a copy of the source's events through
    /// <paramref name="throughSequence"/> (its head when null). When the source is open in this process the copy is taken
    /// between its turns, so it never splits a turn.
    /// </summary>
    /// <exception cref="SessionForkException">The source has no such position, or the position leaves a call or prompt open.</exception>
    public async Task<CorvidSession> ForkSessionAsync(string sourceId, long? throughSequence = null, string surface = "cli",
        SessionSettings? settings = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var id = NewSessionId(surface);
        if (TryGetSession(sourceId, out var source))
            await source.Actor.RunExclusiveAsync<long>(token => Task.FromResult(SessionFork.Fork(Log, sourceId, id, throughSequence, token)),
                cancellationToken).ConfigureAwait(false);
        else
            SessionFork.Fork(Log, sourceId, id, throughSequence, cancellationToken);
        return OpenSession(id, surface, settings);
    }

    public bool TryGetSession(string sessionId, out CorvidSession session) => _sessions.TryGetValue(sessionId, out session!);

    /// <summary>Whether a turn of the session is running in this process now (opened here and mid-turn; a snapshot).</summary>
    public bool IsTurnRunning(string sessionId) => TryGetSession(sessionId, out var session) && session.Actor.TurnRunning;

    /// <summary>
    /// What going back to before the ledger entry whose before checkpoint is <paramref name="checkpointId"/> would revert,
    /// read from the log without opening the session (<see cref="CorvidSession.PlanRollbackTo"/> for an open one). Null
    /// when no visible ledger entry has that checkpoint.
    /// </summary>
    public RestorePlan? PlanRollbackTo(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        return CheckpointService.PlanRollbackTo(SessionProjection.Load(Log, sessionId, cancellationToken: cancellationToken), checkpointId, ChangesFiles);
    }

    /// <summary>
    /// What a rollback to before <paramref name="checkpointId"/> would do to the files, worked out without writing and
    /// without opening the session (the Rewind plan and preview). Null when no ledger entry has the checkpoint or
    /// checkpoints are off. With a turn of the session running here (<see cref="IsTurnRunning"/>), the files are as the
    /// turn has left them so far.
    /// </summary>
    public async Task<RestoreDraft?> DraftRollbackToAsync(string sessionId, string checkpointId, IReadOnlyCollection<string>? only = null,
        CancellationToken cancellationToken = default) =>
        PlanRollbackTo(sessionId, checkpointId, cancellationToken) is { } plan && Checkpoints is { } checkpoints
            ? await checkpoints.DraftAsync(plan, only, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>A call that would have taken a checkpoint: a tool that changes files, running something other than a read-only command.</summary>
    internal bool ChangesFiles(ToolCallIssued call)
    {
        // A tool no longer registered cannot be analysed, so its call counts as one that may have changed files (fail closed).
        if (Tools.List().FirstOrDefault(tool => tool.Name == call.ToolName) is not { } descriptor)
            return true;
        if (!descriptor.Risk.SideEffects.HasFlag(SideEffects.Filesystem))
            return false;
        try
        {
            return CheckpointService.Covers(descriptor, Approvals.Analyze(new ToolAction(descriptor, call.Arguments)));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>Routes a session's approval prompts to its surface until the returned handle is disposed.</summary>
    public IDisposable AttachPrompter(string sessionId, IApprovalPrompter prompter) => _prompters.Attach(sessionId, prompter);

    /// <summary>A model bound for a slot, loading it if needed (for <c>/model</c>).</summary>
    public ModelBinding Bind(string slot, string model) => _composer.Bind(slot, model);

    /// <summary>Whether memory can embed with a model: it embeds only with the <c>embedder.text</c> models configured at start.</summary>
    public bool IsConfigured(string slot, string model) => _composer.IsConfigured(slot, model);

    /// <summary>Waits for background memory indexing to finish (tests, shutdown).</summary>
    public Task MaintenanceIdleAsync()
    {
        lock (_maintenanceGate)
            return _maintenance;
    }

    /// <summary>
    /// Waits at most <paramref name="bound"/> for background work (a surface leaving: <c>corvid chat</c>'s exit). When it is
    /// not done by then, the titles being written are cancelled, so leaving is never held by the generator; a session left
    /// untitled is queued again after its next answered exchange. True when the work finished in time.
    /// </summary>
    public async Task<bool> MaintenanceIdleAsync(TimeSpan bound)
    {
        var idle = MaintenanceIdleAsync();
        // A timeout, like the work's own failure, is not the caller's: suppressed.
        await idle.WaitAsync(bound).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (idle.IsCompleted)
            return true;
        await _titling.CancelAsync().ConfigureAwait(false);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        // A5: children a person started (/delegate, the app) end cancelled and log it before their sessions go.
        await StartedChildrenIdleAsync().ConfigureAwait(false);
        Task? scheduler;
        lock (_schedulerGate)
            scheduler = _scheduler;
        if (scheduler is not null)
            await scheduler.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_learning is not null)
            await _learning.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_lessonLearning is not null)
            await _lessonLearning.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_schedulerGate)
        {
            _schedulerLock?.Dispose();
            _schedulerStop?.Dispose();
        }
        await Sessions.DisposeAsync().ConfigureAwait(false);
        // After the sessions, so no turn of theirs is still writing when another process may take one over (G-12).
        ReleaseDrivers();
        // After the sessions, so no turn is still calling a server; this stops every server process.
        if (Mcp is not null)
            await Mcp.DisposeAsync().ConfigureAwait(false);
        await MaintenanceIdleAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_swap is not null)
            await _swap.DisposeAsync().ConfigureAwait(false);
        foreach (var waiter in _waiting.Values)
            waiter.TrySetCanceled();
        Builtin.Web?.Dispose();
        _composer.Dispose();
        Devices.Dispose();
        _lifetime.Dispose();
        _titling.Dispose();
    }

    internal static string NewSessionId(string surface) =>
        string.Create(CultureInfo.InvariantCulture, $"{surface}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");

    internal async Task<Delivery> SendAsync(CorvidSession session, string text, EnvelopeOrigin origin, CancellationToken cancellationToken)
    {
        var delivery = await PostAsync(session, text, origin, null, cancellationToken).ConfigureAwait(false);
        return await delivery.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts a message and returns once it is queued, with a task for its delivery, so a surface that receives faster than
    /// turns finish (the gateway) keeps each chat's order without waiting for answers.
    /// </summary>
    internal async Task<Task<Delivery>> PostAsync(CorvidSession session, string text, EnvelopeOrigin origin, GuestScope? guest,
        CancellationToken cancellationToken, ShellLine? shell = null)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[messageId] = waiter;
        try
        {
            await Sessions.PostAsync(new Envelope(session.Id, session.Surface, text, origin, messageId) { Guest = guest, Shell = shell },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _waiting.TryRemove(messageId, out _);
            throw;
        }
        _ = waiter.Task.ContinueWith(done => _waiting.TryRemove(messageId, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return waiter.Task;
    }

    // C-006's privacy hook (plan §4.2 step 6): a switch to a gated model checks the conversation first, with the session's taint.
    // A5: a child's own slots (its mix), and every change of a slot told to the surfaces (ModelChanged).
    private SessionModelSlots NewSlots(string sessionId, IReadOnlyDictionary<string, SlotConfiguration>? own = null) =>
        new(Registry, Log, sessionId, new ModelSwitchOptions
        {
            Privacy = new ProjectionPrivacyGate(Log, Egress, id => TryGetSession(id, out var session) && session.Tainted),
            Changed = result => RaiseModelChanged(sessionId, result),
        }, own);

    private SessionActor CreateActor(string sessionId)
    {
        // G-12: every actor runs under the session's driver lock, also one made again for a message posted while the session
        // was being let go of (ReleaseDriverAsync): idempotent when OpenSession already took it.
        TakeDriver(sessionId);
        var settings = _settings.TryGetValue(sessionId, out var chosen) ? chosen : SessionSettings.Default;
        // A5: a child session runs on its plan: its own slots, toolset, budget and approval ceiling.
        var child = _childPlans.GetValueOrDefault(sessionId);
        var slots = NewSlots(sessionId, child?.Configurations);
        var tools = child is not null ? ChildTools(child) : settings.Tools ? Tools : new ToolRegistry();
        var runner = new TurnRunner(sessionId, Services(slots, settings.Interactive, tools, settings, child));
        var actor = new SessionActor(runner, slots, new Sink(this), new SessionActorOptions { EmbedderSwap = _swap });
        _sessions[sessionId] = new CorvidSession(this, sessionId, slots, runner, actor)
        {
            Interactive = settings.Interactive,
            Tools = tools,
            Child = child,
        };
        return actor;
    }

    /// <summary>The built-in tools' examples and the kernel's <c>ask_user</c>'s (K-6), for the shortlist.</summary>
    private static readonly IReadOnlyDictionary<string, ImmutableArray<string>> ToolExamples =
        new Dictionary<string, ImmutableArray<string>>(BuiltinTools.Examples, StringComparer.Ordinal) { [AskUserTool.ToolName] = AskUserTool.Examples, [DelegateTaskTool.ToolName] = DelegateTaskTool.Examples };

    /// <summary>One session's turn services over the shared ones. Scheduled runs are not interactive: nobody can be asked.</summary>
    private TurnServices Services(SessionModelSlots slots, bool interactive, ToolRegistry? tools = null, SessionSettings? session = null,
        ChildPlan? child = null)
    {
        var turn = Options.Turn;
        var prompt = turn.SystemPrompt is { Length: > 0 } configured ? configured : TurnOptions.DefaultSystemPrompt;
        if (session?.Persona is { Length: > 0 } persona)
            prompt = prompt + "\n\n" + persona;
        if (child is not null)
            prompt += "\n\n" + ChildPrompt(child);
        // A5, plan §14: a child writes no memory and learns no skills or lessons; it reads as its parent does.
        var learns = interactive && child is null;
        return new TurnServices
        {
            Log = Log,
            Slots = slots,
            Fabric = Fabric,
            // A5: the session's own decider (its mix's, or a person's switch), else the shared one.
            Deciders = _deciders,
            Tools = tools ?? Tools,
            Approvals = Approvals,
            Memory = child is null ? Memory?.Writer : null,
            Retriever = Memory?.Retriever,
            Checkpoints = Checkpoints,
            // Frozen per session (plan §9.1): an edit made during the session shows from the next one.
            CoreBlocks = Memory?.Blocks.Snapshot(),
            Shortlist = _shortlist,
            Skills = Skills.Store,
            // Unattended job turns are not distilled into skills: nobody saw them.
            SkillLearning = learns ? Skills.Learner : null,
            // C-025: the project scopes recall and lessons; unattended turns draft none, as for skills.
            Project = Memory?.Lessons is null ? null : Project,
            LessonLearning = learns ? Memory?.LessonLearner : null,
            SkillShortlist = _skillShortlist,
            // Turn progress and K-2's running tails, to whichever surface watches the session.
            Listener = Feed.Listener,
            Options = new TurnOptions
            {
                SystemPrompt = prompt,
                // A gateway chat can be answered elsewhere: in a thread, the home channel, or not at all (C-020).
                AskDeliver = child is null && (session?.AskDeliver ?? false),
                MaxSteps = child?.Budget.MaxSteps ?? turn.MaxSteps,
                MaxOutputTokens = turn.MaxOutputTokens,
                // C-029: the six-question ingress (both shortlists) on a smaller state; 600, the default state, turns it off.
                DualShortlistStateBudget = turn.DualShortlistStateTokens < TurnStateBudget.Default.Total
                    ? new TurnStateBudget(Total: turn.DualShortlistStateTokens)
                    : null,
                // A5: a child's mode, never laxer than its parent's when it was started.
                ApprovalMode = child?.ApprovalMode ?? Options.Approvals.Mode,
                Interactive = interactive,
                // A5, plan §14: a child never asks the person a question (no clarify, no ask_user).
                AsksQuestions = child is null,
                // C-005 has no examples file; C-012 wrote the built-in tools' examples by hand.
                ToolExamples = ToolExamples,
            },
        };
    }

    /// <summary>
    /// C-041: a session opened or resumed. The scheduler this runtime holds wakes and looks for due jobs now. A runtime that
    /// lost the lock at start (<see cref="SchedulerState"/> "run by another corvid process") tries it again, so a long-lived
    /// gateway or ACP process takes the scheduler over once the chat that held it has exited, and its first pass runs what
    /// became due meanwhile (missed ticks collapse into one run, as in C-012). Nothing happens after disposal starts.
    /// </summary>
    public void WakeScheduler()
    {
        if (!Options.Scheduler.Enabled)
            return;
        JobScheduler? held;
        bool lost;
        lock (_schedulerGate)
            (held, lost) = (_jobScheduler, SchedulerState == SchedulerElsewhere);
        if (held is not null)
            held.Wake();
        // Only a lost lock is retried: an unwritable lock file ("off: …") stays off until a restart.
        else if (lost)
            TryStartScheduler();
    }

    // C-012's scheduler (plan §13). JobScheduler.RunAsync marks runs another process left running as interrupted, so only
    // one process may run it: an exclusive lock beside the job store decides which, and the others leave it alone. C-041:
    // idempotent and gated, because a session open retries it after the lock was lost; never after disposal starts.
    private void TryStartScheduler()
    {
        lock (_schedulerGate)
        {
            if (_scheduler is not null || _lifetime.IsCancellationRequested)
                return;
            StartSchedulerLocked();
        }
    }

    /// <summary>
    /// Hosts the scheduler in this process from now on (the app's Start hosting, U-8): takes its lock and runs due jobs, or,
    /// while another process holds the lock, waits to take it over (<see cref="WakeScheduler"/> retries). True when it runs
    /// here now. Off with <c>Corvid:Scheduler:Enabled</c> false.
    /// </summary>
    public bool StartScheduler()
    {
        if (!Options.Scheduler.Enabled)
            return false;
        lock (_schedulerGate)
        {
            if (_scheduler is null && !_lifetime.IsCancellationRequested)
                StartSchedulerLocked();
            return _jobScheduler is not null;
        }
    }

    /// <summary>
    /// Stops hosting the scheduler (the app's Stop hosting): the scheduler stops (a run in progress is cancelled and marked
    /// so), its lock is released for another process, and this runtime takes it over no more until <see cref="StartScheduler"/>.
    /// </summary>
    public async Task StopSchedulerAsync()
    {
        Task? running;
        CancellationTokenSource? stop;
        lock (_schedulerGate)
        {
            (running, stop) = (_scheduler, _schedulerStop);
            if (stop is not null)
                stop.Cancel();
        }
        if (running is not null)
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (_schedulerGate)
        {
            // Only what this call stopped: a Start that ran meanwhile keeps its own.
            if (ReferenceEquals(_schedulerStop, stop))
            {
                _schedulerLock?.Dispose();
                _schedulerLock = null;
                _scheduler = null;
                _jobScheduler = null;
                _schedulerStop = null;
                stop?.Dispose();
            }
            if (Options.Scheduler.Enabled && _scheduler is null)
                SchedulerState = SchedulerNotHosted;
        }
    }

    private void StartSchedulerLocked()
    {
        try
        {
            _schedulerLock = ProcessLock.TryAcquire(SchedulerLockPath, attempts: 3);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // An unwritable lock file (a read-only directory, a full disk) should not stop the chat; the jobs simply wait
            // for a process that can run them. Not "run by another process": nobody holds it.
            SchedulerState = "off: " + FileLocks.Unopenable(SchedulerLockPath, exception);
            return;
        }
        if (_schedulerLock is null)
        {
            SchedulerState = SchedulerElsewhere;
            return;
        }
        var scripts = new SandboxedScriptRunner(Builtin.Shell, Approvals, Options.Approvals.Mode);
        var agent = RecordingTaint(new TurnJobAgent(sessionId => Services(NewSlots(sessionId), interactive: false)));
        var scheduler = new JobScheduler(Builtin.Jobs, Fabric, scripts, agent,
            options: new JobSchedulerOptions { Generator = Registry.Get(ModelSlots.Generator).Default.Profile.Id })
        {
            Completed = (job, run, _) =>
            {
                QueueJobResult(job, run);
                _jobCompleted.Raise(job, run);
                return Task.CompletedTask;
            },
        };
        SchedulerState = "running in this process";
        _jobScheduler = scheduler;
        _schedulerStop?.Dispose();
        _schedulerStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var lifetime = _schedulerStop.Token;
        _scheduler = Task.Run(() => scheduler.RunAsync(lifetime));
    }

    /// <summary>
    /// A job created in a gateway chat reports to that chat (plan §13 with §12): its result is queued in the delivery ledger,
    /// which is in the shared event log, so a gateway sends it even when another process (a CLI holding the scheduler) ran
    /// the job. Skipped ticks report nothing; jobs created elsewhere stay local.
    /// </summary>
    /// <summary>The scheduler's agent, remembering which runs ended tainted for <see cref="QueueJobResult"/>.</summary>
    internal IJobAgent RecordingTaint(IJobAgent agent) => new TaintRecordingJobAgent(agent, _taintedRuns);

    internal void QueueJobResult(ScheduledJob job, JobRun run)
    {
        // A run that fetched a page is tainted whoever created the job; its answer is marked so, like an interactive one.
        var ranTainted = _taintedRuns.TryRemove(run.SessionId, out _);
        if (!GatewaySessions.TryParse(job.OriginSession, out var adapter, out var chat))
            return;
        var text = run.Status switch
        {
            JobRunStatus.Ran or JobRunStatus.ScriptOnly when run.Output is { Length: > 0 } output => output,
            JobRunStatus.Ran or JobRunStatus.ScriptOnly or JobRunStatus.Skipped => null,
            _ => $"The job did not complete ({run.Status}{(run.Detail is { Length: > 0 } detail ? ": " + detail : "")}).",
        };
        if (text is null)
            return;
        try
        {
            DeliveryLedger.Queue(Log, new DeliveryQueued("job-" + run.RunId, adapter, chat, $"Scheduled job \"{job.Name}\":\n{text}", "job",
                SourceSession: run.SessionId, Tainted: job.CreatedTainted || job.Script is not null || ranTainted));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The run is recorded in the job store either way; a ledger that cannot be written loses only the message.
        }
    }

    private void Delivered(Delivery delivery)
    {
        // Queued before the sender hears back, so MaintenanceIdleAsync after SendAsync always covers this turn.
        if (Memory is { } memory)
            QueueCatchUp(memory, delivery.Request.SessionId);
        // K-9: after an answered exchange, a session without a title gets one from the local generator, in the background.
        // Not for sessions nobody looks at as a conversation here (an HTTP client's, non-interactive): each would cost a call.
        if (delivery.Result is { Status: TurnStatus.Answered, Text: not null } && Titles is { Unavailable: null, Options.Generate: true }
            && !_titled.ContainsKey(delivery.Request.SessionId)
            && TryGetSession(delivery.Request.SessionId, out var answered) && answered.Interactive && answered.Child is null)
            QueueTitle(delivery.Request.SessionId);
        if (delivery.Request.MessageId is { } id && _waiting.TryRemove(id, out var waiter))
            waiter.TrySetResult(delivery);
    }

    // After each turn, the episodic index catches up with the log (plan §9.1 tier 2) and the new episodes are embedded, in
    // the background so the answer is not held up. Memory is best effort: a failure here never affects the session.
    private void QueueCatchUp(MemoryServices memory, string sessionId)
    {
        lock (_maintenanceGate)
            _maintenance = _maintenance.ContinueWith(async _ =>
            {
                try
                {
                    var added = memory.Episodes.CatchUp(Log, sessionId);
                    if (added.Count > 0)
                        await memory.Vectors.EmbedAsync([.. added.Select(episode => (episode.ItemId, episode.Text))]).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    // On the maintenance chain, so MaintenanceIdleAsync (the CLI before it exits) waits for the title. A title is a
    // convenience: a failure leaves the session titled by its first message.
    private void QueueTitle(string sessionId)
    {
        var (end, leaving) = (_lifetime.Token, _titling.Token);
        lock (_maintenanceGate)
            _maintenance = _maintenance.ContinueWith(async _ =>
            {
                try
                {
                    using var titling = CancellationTokenSource.CreateLinkedTokenSource(end, leaving);
                    var lifetime = titling.Token;
                    if (SessionProjection.Load(Log, sessionId, cancellationToken: lifetime).Title is not null)
                    {
                        _titled[sessionId] = 0;
                        return;
                    }
                    if (await Titles.TitleAsync(sessionId, lifetime).ConfigureAwait(false) is { } titled)
                    {
                        _titled[sessionId] = 0;
                        TitleChanged?.Invoke(sessionId, titled);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private sealed class Sink(CorvidRuntime runtime) : IDeliverySink
    {
        public ValueTask DeliverAsync(Delivery delivery, CancellationToken cancellationToken)
        {
            runtime.Delivered(delivery);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Remembers which job runs ended tainted, for <see cref="CorvidRuntime.QueueJobResult"/>.</summary>
internal sealed class TaintRecordingJobAgent(IJobAgent inner, ConcurrentDictionary<string, byte> tainted) : IJobAgent
{
    public async Task<JobAgentResult> RunAsync(JobAgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await inner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Tainted || request.Tainted)
            tainted[request.SessionId] = 0;
        return result;
    }
}

/// <summary>How a session's turns run, chosen by the surface that opens it (applied when the session is first opened).</summary>
/// <param name="Interactive">False when nobody can answer an approval (an HTTP client): an ask is denied without a prompt.</param>
/// <param name="Tools">False to offer the model no tools at all.</param>
/// <param name="Persona">Added to the system prompt (a gateway channel's persona, plan §12).</param>
/// <param name="AskDeliver">Ask <c>deliver</c> at egress: the surface can answer in a thread, elsewhere, or not at all.</param>
public sealed record SessionSettings(bool Interactive = true, bool Tools = true, string? Persona = null, bool AskDeliver = false)
{
    public static SessionSettings Default { get; } = new();
}

/// <summary>One conversation: its slots, its turn runner and its actor. Surfaces talk to it through this handle.</summary>
public sealed class CorvidSession
{
    private const int MaxAppendAttempts = 8;
    private readonly CorvidRuntime _runtime;

    internal CorvidSession(CorvidRuntime runtime, string id, SessionModelSlots slots, TurnRunner runner, SessionActor actor)
    {
        _runtime = runtime;
        Id = id;
        Slots = slots;
        Runner = runner;
        Actor = actor;
    }

    public string Id { get; }

    public string Surface { get; internal set; } = "cli";

    /// <summary>Someone can answer its approvals (<see cref="SessionSettings.Interactive"/>); an HTTP client's session cannot.</summary>
    public bool Interactive { get; internal init; } = true;

    public SessionModelSlots Slots { get; }

    public SessionActor Actor { get; }

    internal TurnRunner Runner { get; }

    /// <summary>The tools the session's turns may be offered (A5: a child's explicit toolset is its own registry).</summary>
    public ToolRegistry Tools { get; internal init; } = new();

    /// <summary>A5: what this child session was started with, or null for a top-level session.</summary>
    public ChildPlan? Child { get; internal init; }

    /// <summary>A5: the session this one is a child of, or null.</summary>
    public string? ParentId => Child?.ParentId;

    /// <summary>A5: how deep it is: 0 for a top-level session, 1 for a child.</summary>
    public int Depth => Child?.Depth ?? 0;

    /// <summary>Whether outside content is in the session's context (plan §10.3).</summary>
    public bool Tainted => Runner.Tainted;

    /// <summary>
    /// The approval mode this session's calls are assessed under (G-10): <c>Corvid:Approvals:Mode</c> until a surface sets
    /// another for this session; applies from the next call assessed, the running turn's included. Held by this process
    /// only: the session opened again in another process starts at the configured mode. The hardline list applies in
    /// every mode. A5: a child's is never laxer than the mode it was started with (its parent's ceiling).
    /// </summary>
    public ApprovalMode ApprovalMode
    {
        get => Runner.ApprovalMode;
        set => Runner.ApprovalMode = Child is { } child ? ChildSessions.Stricter(value, child.ApprovalMode) : value;
    }

    /// <summary>
    /// A5: switches one slot of this session to a catalogue model (any slot: generator, decider, extractor…), for as long as
    /// <paramref name="scope"/> says: the rest of the session (<see cref="SwitchScope.Session"/>, logged as <c>session</c>;
    /// escalation and fallback still move off it and de-escalation returns to it), the same pinned, or this turn only. A
    /// running turn takes it at its next step boundary, after its issued tool calls. The runtime's defaults (the registry,
    /// <c>corvid.json</c>) are not touched: <see cref="CorvidRuntime.SetDefaultModel"/> changes where new sessions start.
    /// A cloud model passes the privacy gate first (with the session's taint); a refusal is the result, not an exception.
    /// <see cref="CorvidRuntime.ModelChanged"/> reports it.
    /// </summary>
    /// <exception cref="ArgumentException">No such model, or it cannot serve the slot.</exception>
    /// <exception cref="ModelUnavailableException">The model is not usable offline.</exception>
    /// <exception cref="InvalidOperationException">
    /// Memory embeds only with the configured <c>embedder.text</c> models, or (a child) the model is not in <c>Corvid:Agents:Models</c>.
    /// </exception>
    public Task<SwitchResult> SetModelAsync(string slot, string model, SwitchScope scope = SwitchScope.Session,
        CancellationToken cancellationToken = default)
    {
        var binding = _runtime.BindForSession(this, slot, model);
        return Actor.SwitchModelAsync(slot, binding, scope, cancellationToken);
    }

    /// <summary>
    /// A5: forgets the person's choice for a slot in this session: its default is the configured one again, and an unpinned
    /// slot still on the choice switches back now. Null when there was no choice.
    /// </summary>
    public Task<SwitchResult?> ClearModelChoiceAsync(string slot, CancellationToken cancellationToken = default)
    {
        ModelSlots.ContractOf(slot);
        return Slots.ClearSessionDefaultAsync(slot, cancellationToken);
    }

    /// <summary>Runs one turn and waits for its delivery.</summary>
    public Task<Delivery> SendAsync(string text, CancellationToken cancellationToken = default) =>
        _runtime.SendAsync(this, text, EnvelopeOrigin.User, cancellationToken);

    /// <summary>
    /// Queues a message and returns once it is queued, with a task for its delivery. <see cref="EnvelopeOrigin.External"/>
    /// marks it as someone else's (another gateway user's), which taints the session (plan §10.3).
    /// </summary>
    public Task<Task<Delivery>> PostAsync(string text, EnvelopeOrigin origin, CancellationToken cancellationToken = default) =>
        _runtime.PostAsync(this, text, origin, null, cancellationToken);

    /// <summary>
    /// Queues a guest's message (C-020): outside content, whose turn is offered only the tools <paramref name="guest"/> names
    /// and none of the user's skills or memory.
    /// </summary>
    public Task<Task<Delivery>> PostAsGuestAsync(string text, GuestScope guest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guest);
        return _runtime.PostAsync(this, text, EnvelopeOrigin.External, guest, cancellationToken);
    }

    /// <summary>
    /// The composer's <c>!</c> line (A4): queues a turn that runs <paramref name="shell"/>'s command as the user's own
    /// <c>shell</c> call (approvals, sandbox, checkpoints) and asks the generator nothing; <paramref name="line"/> is the line
    /// as typed, which the log keeps as the message. Returns once queued, with a task for its delivery.
    /// </summary>
    public Task<Task<Delivery>> PostShellAsync(string line, ShellLine shell, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        ArgumentNullException.ThrowIfNull(shell);
        return _runtime.PostAsync(this, line, EnvelopeOrigin.User, null, cancellationToken, shell);
    }

    /// <summary>
    /// The composer's <c>#</c> line (A4): <paramref name="text"/> through memory's gated write path, as the user's request.
    /// Without a calibrated decider it is staged for <c>/memory pending</c>; in a tainted session it is staged whatever the
    /// decider says (plan §10.3); a credential is refused and stored nowhere.
    /// </summary>
    /// <exception cref="InvalidOperationException">Memory is off, or there is no extractor to write with.</exception>
    public Task<MemoryWriteResult> RememberAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (_runtime.Memory?.Writer is not { } writer)
            throw new InvalidOperationException(_runtime.Memory is null
                ? "Memory is off (Corvid:Memory:Enabled), so nothing was staged."
                : "Memory writes need the extractor slot; bind one to stage and review writes.");
        var generator = Slots.TryCurrent(ModelSlots.Generator, out var binding) ? binding.Profile.Id : "unknown";
        return writer.WriteAsync(new MemoryWriteRequest(Id, text.Trim(), Surface: Surface, Generator: generator, Tainted: Tainted),
            cancellationToken);
    }

    /// <summary>Cancels the running turn (Ctrl-C, <c>/cancel</c>).</summary>
    public void Cancel() => Actor.CancelTurn();

    /// <summary>
    /// K-3, steer now: the person's message for the running turn, taken at its next step checkpoint. True once taken; false
    /// when no turn was running or it ended first, and the caller sends the text as an ordinary message instead.
    /// </summary>
    /// <param name="withdraw">Takes the steer back while it still waits (the person removed it from the queue).</param>
    public Task<bool> SteerAsync(string text, CancellationToken withdraw = default) => Actor.SteerAsync(text, withdraw);

    public SessionSnapshot Snapshot() => SessionProjection.Load(_runtime.Log, Id);

    /// <summary>The session's write ledger still in the transcript, oldest first (<c>/rollback</c> lists it).</summary>
    public ImmutableArray<LedgerEntry> Checkpoints() => CheckpointService.Ledger(Snapshot());

    /// <summary>
    /// <c>/undo</c> (C-014): reverts the last turn's file changes and moves the transcript to before its message. Runs when
    /// no turn is running. Null when there is no turn to undo.
    /// </summary>
    public Task<RestoreResult?> UndoAsync(CancellationToken cancellationToken = default) =>
        RestoreAsync(snapshot => CheckpointService.PlanUndo(snapshot, ChangesFiles), cancellationToken);

    /// <summary>
    /// <c>/compact</c> (C-035): compacts the conversation before the latest turn now (<see cref="TurnRunner.CompactNowAsync(CancellationToken)"/>).
    /// On the actor, like <see cref="UndoAsync"/>: it waits for a running turn and goes ahead of queued messages.
    /// </summary>
    public Task<CompactionResult> CompactAsync(CancellationToken cancellationToken = default) => CompactAsync(null, cancellationToken);

    /// <summary><c>/compact &lt;focus&gt;</c> (C-041): as <see cref="CompactAsync(CancellationToken)"/>, keeping what the user named.</summary>
    public Task<CompactionResult> CompactAsync(string? focus, CancellationToken cancellationToken = default) =>
        Actor.RunExclusiveAsync(token => Runner.CompactNowAsync(focus, token), cancellationToken);

    /// <summary><c>/rollback N</c>: back to before the Nth latest checkpoint's call. Null when there are fewer checkpoints.</summary>
    public Task<RestoreResult?> RollbackAsync(int number, CancellationToken cancellationToken = default) =>
        RestoreAsync(snapshot => CheckpointService.PlanRollback(snapshot, number, ChangesFiles), cancellationToken);

    private Task<RestoreResult?> RestoreAsync(Func<SessionSnapshot, RestorePlan?> plan, CancellationToken cancellationToken) =>
        // On the actor, so no turn changes files or the log while the restore decides and writes.
        Actor.RunExclusiveAsync<RestoreResult?>(async token =>
        {
            if (plan(SessionProjection.Load(_runtime.Log, Id, cancellationToken: token)) is not { } chosen)
                return null;
            return _runtime.Checkpoints is { } checkpoints
                ? await checkpoints.RestoreAsync(_runtime.Log, Id, chosen, token).ConfigureAwait(false)
                : CheckpointService.Rewind(_runtime.Log, Id, chosen, token);
        }, cancellationToken);

    /// <summary>
    /// What going back to before the ledger entry whose before checkpoint is <paramref name="checkpointId"/> would revert
    /// (the plan <c>/rollback</c> makes for it, with the same check for calls that changed files unrecorded). Null when
    /// no visible ledger entry has that checkpoint.
    /// </summary>
    public RestorePlan? PlanRollbackTo(string checkpointId) => CheckpointService.PlanRollbackTo(Snapshot(), checkpointId, ChangesFiles);

    /// <summary>
    /// What a rollback to before <paramref name="checkpointId"/> would do to the files, worked out without writing (the
    /// Rewind preview and plan). Null when no ledger entry has the checkpoint or checkpoints are off.
    /// </summary>
    public async Task<RestoreDraft?> DraftRollbackToAsync(string checkpointId, IReadOnlyCollection<string>? only = null,
        CancellationToken cancellationToken = default) =>
        PlanRollbackTo(checkpointId) is { } plan && _runtime.Checkpoints is { } checkpoints
            ? await checkpoints.DraftAsync(plan, only, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Rolls back to before <paramref name="checkpointId"/> with a scope (the windowed app's Rewind and Review): files and
    /// conversation as <c>/rollback</c> does, the conversation only, or the files only, or with <paramref name="only"/>
    /// just those files (G-3). On the actor, like <see cref="RollbackAsync"/>. Null when no ledger entry has the checkpoint.
    /// </summary>
    public Task<RestoreResult?> RollbackToAsync(string checkpointId, RestoreScope scope, IReadOnlyCollection<string>? only = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        return Actor.RunExclusiveAsync<RestoreResult?>(async token =>
        {
            if (CheckpointService.PlanRollbackTo(SessionProjection.Load(_runtime.Log, Id, cancellationToken: token), checkpointId, ChangesFiles) is not { } plan)
                return null;
            if (_runtime.Checkpoints is { } checkpoints)
                return await checkpoints.RestoreAsync(_runtime.Log, Id, plan, scope, only, token).ConfigureAwait(false);
            // Without checkpoints only the transcript can move.
            return scope switch
            {
                RestoreScope.FilesAndConversation => CheckpointService.Rewind(_runtime.Log, Id, plan, token),
                RestoreScope.ConversationOnly => CheckpointService.RewindConversation(_runtime.Log, Id, plan, token),
                _ => new RestoreResult(RestoreStatus.Refused, plan, [], [], [], "checkpoints are off, so no file can be put back"),
            };
        }, cancellationToken);
    }

    private bool ChangesFiles(ToolCallIssued call) => _runtime.ChangesFiles(call);

    /// <summary>
    /// Starts an empty session with a conversation held elsewhere (an HTTP client that sends its whole history): the user's
    /// and the assistant's messages in order, appended as they were, before the session's first turn.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session already has events.</exception>
    public void SeedHistory(IReadOnlyList<(bool FromUser, string Text)> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            return;
        var events = messages.Select(message => EventData.Create(message.FromUser ? new UserMessage(message.Text) : new AssistantMessage(message.Text))).ToList();
        try
        {
            _runtime.Log.Append(Id, 0, events);
        }
        catch (EventLogConflictException)
        {
            throw new InvalidOperationException($"Session '{Id}' already has a history.");
        }
    }

    /// <summary>Sets or clears the standing goal (plan §13): a <c>GoalSet</c> event, so it survives compaction and restarts.</summary>
    public void SetGoal(string? goal)
    {
        var events = new[] { EventData.Create(new GoalSet(string.IsNullOrWhiteSpace(goal) ? null : goal.Trim())) };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _runtime.Log.Append(Id, _runtime.Log.GetHead(Id), events);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }
}

/// <summary>
/// The approval pipeline's prompter for every session: it hands each prompt to the surface attached to that session. With
/// none attached the prompt fails, which the pipeline records and treats as a denial; it is never answered on the user's behalf.
/// </summary>
internal sealed class ApprovalRouter : IApprovalPrompter
{
    private readonly ConcurrentDictionary<string, IApprovalPrompter> _prompters = new(StringComparer.Ordinal);
    // A5: a child session's prompts go to whatever answers its parent's, unless a surface attached to the child itself.
    private readonly ConcurrentDictionary<string, string> _parents = new(StringComparer.Ordinal);

    public IDisposable Attach(string sessionId, IApprovalPrompter prompter)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        _prompters[sessionId] = prompter;
        return new Detach(this, sessionId, prompter);
    }

    /// <summary>A5: routes a child's prompts to its parent's prompter until the child is forgotten.</summary>
    public void SetParent(string childId, string parentId) => _parents[childId] = parentId;

    public void Forget(string childId) => _parents.TryRemove(childId, out _);

    public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) =>
        _prompters.TryGetValue(prompt.SessionId, out var prompter)
        || _parents.TryGetValue(prompt.SessionId, out var parent) && _prompters.TryGetValue(parent, out prompter)
            ? prompter.AskAsync(prompt, cancellationToken)
            : throw new InvalidOperationException($"No surface is attached to session '{prompt.SessionId}' to answer the approval.");

    private sealed class Detach(ApprovalRouter router, string sessionId, IApprovalPrompter prompter) : IDisposable
    {
        public void Dispose() => router._prompters.TryRemove(KeyValuePair.Create(sessionId, prompter));
    }
}
