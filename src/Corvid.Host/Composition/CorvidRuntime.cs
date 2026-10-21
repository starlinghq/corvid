using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Abstractions.Tokens;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Checkpoints;
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
    MemoryWriter? Writer);

/// <summary>
/// One running agent: the event log, the slots, the fabric, approvals, memory, tools and the session host, composed from
/// configuration. Surfaces open sessions on it and post messages; every session shares the process-wide services and has its
/// own slots and turn runner.
/// </summary>
public sealed class CorvidRuntime : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CorvidSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Delivery>> _waiting = new(StringComparer.Ordinal);
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
    }

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
            ? $"{calibrated}, {escalation} for gates below their floor; degraded tier when Kev declines"
            : $"{calibrated}; degraded tier when Kev declines"
        : "degraded tier (no calibrated decider)";

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
    public CorvidSession OpenSession(string? sessionId = null, string surface = "cli")
    {
        var id = sessionId ?? NewSessionId(surface);
        Sessions.GetOrCreate(id);
        var session = _sessions[id];
        session.Surface = surface;
        return session;
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

    internal Task<Delivery> SendAsync(CorvidSession session, string text, EnvelopeOrigin origin, CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[messageId] = waiter;
        return PostAndWaitAsync();

        async Task<Delivery> PostAndWaitAsync()
        {
            try
            {
                await Sessions.PostAsync(new Envelope(session.Id, session.Surface, text, origin, messageId), cancellationToken).ConfigureAwait(false);
                return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _waiting.TryRemove(messageId, out _);
            }
        }
    }

    // C-006's privacy hook (plan §4.2 step 6): a switch to a gated model checks the conversation first, with the session's taint.
    private SessionModelSlots NewSlots(string sessionId) => new(Registry, Log, sessionId, new ModelSwitchOptions
    {
        Privacy = new ProjectionPrivacyGate(Log, Egress, id => TryGetSession(id, out var session) && session.Tainted),
    });

    private SessionActor CreateActor(string sessionId)
    {
        var slots = NewSlots(sessionId);
        var runner = new TurnRunner(sessionId, Services(slots, interactive: true));
        var actor = new SessionActor(runner, slots, new Sink(this), new SessionActorOptions { EmbedderSwap = _swap });
        _sessions[sessionId] = new CorvidSession(this, sessionId, slots, runner, actor);
        return actor;
    }

    /// <summary>One session's turn services over the shared ones. Scheduled runs are not interactive: nobody can be asked.</summary>
    private TurnServices Services(SessionModelSlots slots, bool interactive)
    {
        var turn = Options.Turn;
        return new TurnServices
        {
            Log = Log,
            Slots = slots,
            Fabric = Fabric,
            Tools = Tools,
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
            SkillShortlist = _skillShortlist,
            Options = new TurnOptions
            {
                SystemPrompt = turn.SystemPrompt is { Length: > 0 } prompt ? prompt : TurnOptions.DefaultSystemPrompt,
                MaxSteps = turn.MaxSteps,
                MaxOutputTokens = turn.MaxOutputTokens,
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
        var agent = new TurnJobAgent(sessionId => Services(NewSlots(sessionId), interactive: false));
        var scheduler = new JobScheduler(Builtin.Jobs, Fabric, scripts, agent,
            options: new JobSchedulerOptions { Generator = Registry.Get(ModelSlots.Generator).Default.Profile.Id })
        {
            Completed = (job, run, _) =>
            {
                JobCompleted?.Invoke(job, run);
                return Task.CompletedTask;
            },
        };
        SchedulerState = "running in this process";
        _scheduler = Task.Run(() => scheduler.RunAsync(_lifetime.Token));
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
