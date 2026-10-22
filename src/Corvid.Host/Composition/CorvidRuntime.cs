using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Abstractions.Tokens;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Gateway;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
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
public sealed class CorvidRuntime : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CorvidSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Delivery>> _waiting = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionSettings> _settings = new(StringComparer.Ordinal);
    // Job runs whose turn ended tainted, by run session, until QueueJobResult takes them. The scheduler and its completion
    // callback run in this process, so the job store needs no column for it.
    private readonly ConcurrentDictionary<string, byte> _taintedRuns = new(StringComparer.Ordinal);
    private readonly ModelComposer _composer;
    private readonly ApprovalRouter _prompters;
    private readonly EmbedderSwapJob? _swap;
    private readonly ToolShortlist _shortlist = new();
    private readonly ToolShortlist _skillShortlist = new(new ShortlistOptions(Size: 3));
    private readonly Lock _maintenanceGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _maintenance = Task.CompletedTask;
    private FileStream? _schedulerLock;
    private Task? _scheduler;
    private Task? _learning;
    private Task? _lessonLearning;

    internal CorvidRuntime(CorvidOptions options, ModelCatalogue catalogue, ModelComposer composer, DeviceSchedulers devices,
        ModelMetrics metrics, TurnFeed feed, SqliteEventLog log, ModelSlotRegistry registry, DecisionFabric fabric,
        ApprovalPipeline approvals, ApprovalRouter prompters, ToolRegistry tools, MemoryServices? memory, ITokenCounter? counter,
        BuiltinServices builtin, CheckpointService? checkpoints = null, SkillServices? skills = null, McpClients? mcp = null)
    {
        Mcp = mcp;
        Checkpoints = checkpoints;
        Skills = skills ?? new SkillServices(null, null, null, null, "off");
        Options = options;
        Catalogue = catalogue;
        _composer = composer;
        Devices = devices;
        Metrics = metrics;
        Feed = feed;
        Log = log;
        Registry = registry;
        Fabric = fabric;
        Approvals = approvals;
        _prompters = prompters;
        Tools = tools;
        Memory = memory;
        Counter = counter;
        Builtin = builtin;
        _swap = memory is null ? null : new EmbedderSwapJob(memory.Vectors);
        Sessions = new SessionHost(CreateActor);
        if (memory is not null && registry.TryGet(ModelSlots.TextEmbedder, out var embedder)
            && memory.Vectors.State.Active != embedder.Default.Profile.Id)
            // The configured embedder changed since the store was written (or the store is new): §4.3's swap, in the background.
            _swap!.Start(embedder.Default.Profile.Id);
        if (options.Scheduler.Enabled)
            StartScheduler();
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

    /// <summary>Whether this process runs due jobs, and if not, why.</summary>
    public string SchedulerState { get; private set; } = "off (Corvid:Scheduler:Enabled)";

    /// <summary>Raised after each scheduled run is recorded; a surface can say so (delivery to chats is C-020's).</summary>
    public event Action<ScheduledJob, JobRun>? JobCompleted;

    internal ITokenCounter? Counter { get; }

    /// <summary>A new session, or an earlier one by id: its transcript is in the log, so the next turn sees it.</summary>
    /// <param name="settings">How the session's turns run; applied when this process first opens the session.</param>
    public CorvidSession OpenSession(string? sessionId = null, string surface = "cli", SessionSettings? settings = null)
    {
        var id = sessionId ?? NewSessionId(surface);
        if (settings is not null)
            _settings.TryAdd(id, settings);
        Sessions.GetOrCreate(id);
        _settings.TryRemove(id, out _);
        var session = _sessions[id];
        session.Surface = surface;
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

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_scheduler is not null)
            await _scheduler.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_learning is not null)
            await _learning.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_lessonLearning is not null)
            await _lessonLearning.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _schedulerLock?.Dispose();
        await Sessions.DisposeAsync().ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[messageId] = waiter;
        try
        {
            await Sessions.PostAsync(new Envelope(session.Id, session.Surface, text, origin, messageId) { Guest = guest }, cancellationToken).ConfigureAwait(false);
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
    private SessionModelSlots NewSlots(string sessionId) => new(Registry, Log, sessionId, new ModelSwitchOptions
    {
        Privacy = new ProjectionPrivacyGate(Log, Egress, id => TryGetSession(id, out var session) && session.Tainted),
    });

    private SessionActor CreateActor(string sessionId)
    {
        var slots = NewSlots(sessionId);
        var settings = _settings.TryGetValue(sessionId, out var chosen) ? chosen : SessionSettings.Default;
        var runner = new TurnRunner(sessionId, Services(slots, settings.Interactive, settings.Tools ? Tools : new ToolRegistry(), settings));
        var actor = new SessionActor(runner, slots, new Sink(this), new SessionActorOptions { EmbedderSwap = _swap });
        _sessions[sessionId] = new CorvidSession(this, sessionId, slots, runner, actor);
        return actor;
    }

    /// <summary>One session's turn services over the shared ones. Scheduled runs are not interactive: nobody can be asked.</summary>
    private TurnServices Services(SessionModelSlots slots, bool interactive, ToolRegistry? tools = null, SessionSettings? session = null)
    {
        var turn = Options.Turn;
        var prompt = turn.SystemPrompt is { Length: > 0 } configured ? configured : TurnOptions.DefaultSystemPrompt;
        if (session?.Persona is { Length: > 0 } persona)
            prompt = prompt + "\n\n" + persona;
        return new TurnServices
        {
            Log = Log,
            Slots = slots,
            Fabric = Fabric,
            Tools = tools ?? Tools,
            Approvals = Approvals,
            Memory = Memory?.Writer,
            Retriever = Memory?.Retriever,
            Checkpoints = Checkpoints,
            // Frozen per session (plan §9.1): an edit made during the session shows from the next one.
            CoreBlocks = Memory?.Blocks.Snapshot(),
            Shortlist = _shortlist,
            Skills = Skills.Store,
            // Unattended job turns are not distilled into skills: nobody saw them.
            SkillLearning = interactive ? Skills.Learner : null,
            // C-025: the project scopes recall and lessons; unattended turns draft none, as for skills.
            Project = Memory?.Lessons is null ? null : Project,
            LessonLearning = interactive ? Memory?.LessonLearner : null,
            SkillShortlist = _skillShortlist,
            // C-033: a confident injection verdict over an MCP tool's description holds the tool in the MCP clients.
            DescriptionReview = Mcp,
            Options = new TurnOptions
            {
                SystemPrompt = prompt,
                // A gateway chat can be answered elsewhere: in a thread, the home channel, or not at all (C-020).
                AskDeliver = session?.AskDeliver ?? false,
                MaxSteps = turn.MaxSteps,
                MaxOutputTokens = turn.MaxOutputTokens,
                // C-029: the six-question ingress (both shortlists) on a smaller state; 600, the default state, turns it off.
                DualShortlistStateBudget = turn.DualShortlistStateTokens < TurnStateBudget.Default.Total
                    ? new TurnStateBudget(Total: turn.DualShortlistStateTokens)
                    : null,
                ApprovalMode = Options.Approvals.Mode,
                Interactive = interactive,
                // C-005 has no examples file; C-012 wrote the built-in tools' examples by hand.
                ToolExamples = BuiltinTools.Examples,
            },
        };
    }

    // C-012's scheduler (plan §13). JobScheduler.RunAsync marks runs another process left running as interrupted, so only
    // one process may run it: an exclusive lock beside the job store decides which, and the others leave it alone.
    private void StartScheduler()
    {
        try
        {
            _schedulerLock = new FileStream(Builtin.Jobs.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            SchedulerState = "run by another corvid process";
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            // An unwritable lock file should not stop the chat; the jobs simply wait for a process that can run them.
            SchedulerState = "off: " + exception.Message;
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
                JobCompleted?.Invoke(job, run);
                return Task.CompletedTask;
            },
        };
        SchedulerState = "running in this process";
        _scheduler = Task.Run(() => scheduler.RunAsync(_lifetime.Token));
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

    public SessionModelSlots Slots { get; }

    public SessionActor Actor { get; }

    internal TurnRunner Runner { get; }

    /// <summary>Whether outside content is in the session's context (plan §10.3).</summary>
    public bool Tainted => Runner.Tainted;

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

    /// <summary>Cancels the running turn (Ctrl-C, <c>/cancel</c>).</summary>
    public void Cancel() => Actor.CancelTurn();

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
    /// <c>/compact</c> (C-035): compacts the conversation before the latest turn now (<see cref="TurnRunner.CompactNowAsync"/>).
    /// On the actor, like <see cref="UndoAsync"/>: it waits for a running turn and goes ahead of queued messages.
    /// </summary>
    public Task<CompactionResult> CompactAsync(CancellationToken cancellationToken = default) =>
        Actor.RunExclusiveAsync(token => Runner.CompactNowAsync(token), cancellationToken);

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

    /// <summary>A call that would have taken a checkpoint: a tool that changes files, running something other than a read-only command.</summary>
    private bool ChangesFiles(ToolCallIssued call)
    {
        // A tool no longer registered cannot be analysed, so its call counts as one that may have changed files (fail closed).
        if (_runtime.Tools.List().FirstOrDefault(tool => tool.Name == call.ToolName) is not { } descriptor)
            return true;
        if (!descriptor.Risk.SideEffects.HasFlag(SideEffects.Filesystem))
            return false;
        try
        {
            return CheckpointService.Covers(descriptor, _runtime.Approvals.Analyze(new ToolAction(descriptor, call.Arguments)));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

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

    public IDisposable Attach(string sessionId, IApprovalPrompter prompter)
    {
        ArgumentNullException.ThrowIfNull(prompter);
        _prompters[sessionId] = prompter;
        return new Detach(this, sessionId, prompter);
    }

    public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) =>
        _prompters.TryGetValue(prompt.SessionId, out var prompter)
            ? prompter.AskAsync(prompt, cancellationToken)
            : throw new InvalidOperationException($"No surface is attached to session '{prompt.SessionId}' to answer the approval.");

    private sealed class Detach(ApprovalRouter router, string sessionId, IApprovalPrompter prompter) : IDisposable
    {
        public void Dispose() => router._prompters.TryRemove(KeyValuePair.Create(sessionId, prompter));
    }
}
