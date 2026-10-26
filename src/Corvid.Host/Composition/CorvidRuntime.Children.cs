using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Host.Commands;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Scheduling;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Tools;

namespace Corvid.Host.Composition;

/// <summary>
/// A5: model choices per session and child sessions (plan §14, <c>delegate_task</c>).
/// <para>
/// Models: <see cref="CorvidSession.SetModelAsync"/> switches one session (its default for the session, pinned, or one turn);
/// <see cref="SetDefaultModel"/> changes the registry, where new sessions start; <see cref="ModelChanged"/> reports every
/// switch made in this process, any slot, any reason.
/// </para>
/// <para>
/// Children: <see cref="PlanChildAsync"/> turns a request into a <see cref="ChildPlan"/> within the parent's ceilings (or
/// refuses it); <see cref="OpenChildSession"/> starts the child session (its log begins with <c>ChildOf</c>, the parent's
/// gets <c>ChildSpawned</c>) under its own driver lock; <see cref="RunChildAsync(CorvidSession, CancellationToken)"/> runs
/// its goal as one turn within its budget, checks the answer against the output schema, logs <c>ChildFinished</c> on the
/// parent and lets the child go. <see cref="RunChildAsync(string, ChildRequest, CancellationToken)"/> does all three.
/// </para>
/// </summary>
public sealed partial class CorvidRuntime
{
    /// <summary>The longest goal a child is given, in characters.</summary>
    public const int MaxChildGoalChars = 16_000;

    /// <summary>The most of a child's answer <c>ChildFinished</c> keeps, in characters.</summary>
    public const int MaxChildTextChars = 4_000;

    private readonly ConcurrentDictionary<string, ChildPlan> _childPlans = new(StringComparer.Ordinal);
    // A5 (/agents stop, the Board): children a person stopped, so their end says so.
    private readonly ConcurrentDictionary<string, byte> _stoppedChildren = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChildUsage> _childUsage = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _childCounts = new(StringComparer.Ordinal);
    private readonly Lock _childGate = new();

    /// <summary>The clock a child's wall-time budget runs on (tests set a fake one).</summary>
    internal TimeProvider ChildClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// A5: a slot of a session changed model, or the person's choice of it for the session changed: every switch made in this
    /// process, any slot and reason, in a turn or between turns. Raised on the switching thread, inside the session's switch
    /// lock: handlers must be quick and thread-safe, must not switch again, and should marshal to their own thread.
    /// </summary>
    public event Action<string, ModelChanged>? ModelChanged;

    /// <summary>
    /// A5: a child of a session started (<see cref="ChildStarted"/>) or ended (<see cref="ChildEnded"/>), by the parent's id.
    /// Raised on the thread that started or ran it; handlers must be thread-safe.
    /// </summary>
    public event Action<string, SessionStreamUpdate>? ChildChanged;

    /// <summary>The decider per session (A5): which fabric, and so which decider, a session's decisions run on now.</summary>
    public SessionDeciders Deciders => _deciders;

    private void RaiseModelChanged(string sessionId, SwitchResult result)
    {
        if (ModelChanged is { } handlers)
            handlers(sessionId, ModelChanges.FromResult(result));
    }

    /// <summary>
    /// A5: the binding <see cref="CorvidSession.SetModelAsync"/> switches a session to: the catalogue model checked for the
    /// slot and loaded, at interactive priority (a child's at subagent priority, within <c>Corvid:Agents:Models</c>).
    /// </summary>
    internal ModelBinding BindForSession(CorvidSession session, string slot, string model)
    {
        ArgumentNullException.ThrowIfNull(session);
        ModelSlots.ContractOf(slot);
        var descriptor = Catalogue.Find(model);
        if (!descriptor.Serves(slot))
            throw new ArgumentException($"'{TerminalText.Visible(descriptor.Name)}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.",
                nameof(model));
        if (slot == ModelSlots.TextEmbedder && !IsConfigured(slot, descriptor.Name))
            throw new InvalidOperationException($"Memory embeds only with the embedder.text models configured at start. Add " +
                $"{TerminalText.Visible(descriptor.Name)} to Corvid:Models:Slots:embedder.text:Fallbacks, then restart.");
        if (session.Child is not null && !AllowedForChildren(descriptor.Name))
            throw new InvalidOperationException($"{TerminalText.Visible(descriptor.Name)} is not in Corvid:Agents:Models, so a child cannot be given it.");
        var availability = Catalogue.Check(descriptor.Name);
        if (!availability.IsReady)
            throw new ModelUnavailableException(availability);
        return _composer.Bind(slot, descriptor.Name, session.Child is null ? DevicePriority.Interactive : DevicePriority.Subagent);
    }

    /// <summary>
    /// A5: the runtime's default for a slot from now on (the registry), apart from any session's own choice: sessions opened
    /// later start on it, and sessions that chose nothing for the slot return to it when their turns end. The slot's ladder and
    /// fallbacks stay, without the model itself. Nothing is written to <c>corvid.json</c> (the config writer does that).
    /// </summary>
    /// <exception cref="ArgumentException">No such model, or it cannot serve the slot.</exception>
    /// <exception cref="ModelUnavailableException">The model is not usable offline.</exception>
    public ModelBinding SetDefaultModel(string slot, string model)
    {
        ModelSlots.ContractOf(slot);
        var descriptor = Catalogue.Find(model);
        if (!descriptor.Serves(slot))
            throw new ArgumentException($"{TerminalText.Visible(descriptor.Name)} cannot serve as the {slot}.", nameof(model));
        var availability = Catalogue.Check(descriptor.Name);
        if (!availability.IsReady)
            throw new ModelUnavailableException(availability);
        var binding = Bind(slot, descriptor.Name);
        if (Registry.TryGet(slot, out var configured))
            Registry.Bind(slot, binding, configured.Escalation.Where(step => !ReferenceEquals(step, binding)),
                configured.Fallbacks.Where(step => !ReferenceEquals(step, binding)));
        else
            Registry.Bind(slot, binding);
        return binding;
    }

    private bool AllowedForChildren(string model) =>
        Options.Agents.Models.Count == 0 || Options.Agents.Models.Contains(model, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A5: a child request checked and clamped to the parent's ceilings, without starting anything: the plan a spawn's
    /// approval card shows (goal, toolset, budget, model mix) and <see cref="OpenChildSession"/> starts. A model or a person
    /// can only ask for less than the ceilings: steps at most <c>Corvid:Agents:MaxSteps</c> and <c>Corvid:Turn:MaxSteps</c>,
    /// tokens and seconds at most <c>Corvid:Agents</c>', tools only from the parent session (and the calling turn's offer),
    /// an approval mode no laxer than the parent's. Every model the mix names is checked against the catalogue, the slot,
    /// <c>Corvid:Agents:Models</c> and its availability, and a gated (cloud) one against the privacy egress gate with the
    /// goal and the parent's taint: a default the gate would refuse refuses the child, a ladder or fallback step it would
    /// refuse is left out. Slots the mix leaves out run on the parent's session defaults and ladders, at subagent priority.
    /// </summary>
    /// <exception cref="ChildRefusedException">Why not, in a sentence; names in it are shown with their hidden characters escaped.</exception>
    public Task<ChildPlan> PlanChildAsync(string parentId, ChildRequest request, CancellationToken cancellationToken = default) =>
        PlanChildAsync(parentId, request, preview: false, cancellationToken);

    /// <summary>
    /// <paramref name="preview"/>: for an approval card, which waits on the person: the mix is checked against the catalogue,
    /// its availability and the egress gate on the catalogue's profiles, and nothing is loaded or bound. The plan has no
    /// <see cref="ChildPlan.Configurations"/> and is never opened; the approved call plans again and loads then.
    /// </summary>
    private async Task<ChildPlan> PlanChildAsync(string parentId, ChildRequest request, bool preview, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        ArgumentNullException.ThrowIfNull(request);
        if (!TryGetSession(parentId, out var parent))
            throw new ChildRefusedException($"Session {TerminalText.Visible(parentId)} is not open in this process: open it before starting a child.");
        var agents = Options.Agents;
        if (agents.MaxDepth == 0)
            throw new ChildRefusedException("Delegation is off: Corvid:Agents:MaxDepth is 0.");
        if (parent.Depth >= agents.MaxDepth)
            throw new ChildRefusedException("A child session cannot start children of its own.");
        var goal = request.Goal?.Trim() ?? "";
        if (goal.Length == 0)
            throw new ChildRefusedException("A child needs a goal.");
        if (goal.Length > MaxChildGoalChars)
            throw new ChildRefusedException($"The goal is {goal.Length} characters; a child takes at most {MaxChildGoalChars}.");
        if (request.OutputSchema is { } schema && !OutputSchema.IsSupported(schema, out var why))
            throw new ChildRefusedException($"The output schema is not one Corvid can check: {why}.");
        if (request.By is not (ChildSessions.ByAgent or ChildSessions.ByUser))
            throw new ChildRefusedException($"A child is started by \"{ChildSessions.ByAgent}\" or \"{ChildSessions.ByUser}\".");

        var budget = new ChildBudget(
            Clamp(request.MaxSteps, agents.MaxSteps, Math.Min(agents.MaxSteps, Options.Turn.MaxSteps)),
            Clamp(request.MaxTokens, agents.MaxTokens, agents.MaxTokens),
            Clamp(request.MaxSeconds, agents.MaxSeconds, agents.MaxSeconds));
        var (functions, dropped) = ChildToolset(parent, request);

        var mix = ModelMix.Empty;
        try
        {
            if (request.MixName is { } name)
                mix = agents.Mix(name) ?? throw new ChildRefusedException($"No mix is named \"{TerminalText.Visible(name)}\" in Corvid:Agents:Mixes.");
        }
        catch (ModelMixException exception)
        {
            throw new ChildRefusedException($"The configured mix \"{TerminalText.Visible(request.MixName!)}\" is malformed: {exception.Message}");
        }
        // Security M1 (A5 review): the decider judges the child's approvals and checkpoints, so the agent never picks it nor
        // turns its escalation off. A child's decider is its parent's, unless the person (/delegate, the app) or a configured
        // Corvid:Agents:Mixes entry names another.
        if (request.By == ChildSessions.ByAgent && request.Mix is { } asked && asked.Slots.ContainsKey(ModelSlots.Decider))
            throw new ChildRefusedException("A child's decider is this session's: the agent cannot choose it or turn its escalation off. " +
                "Leave the decider out of the mix (the person can choose one with /delegate, or name a mix configured in Corvid:Agents:Mixes).");
        mix = mix.With(request.Mix ?? ModelMix.Empty);
        var tainted = parent.Tainted;
        var (configurations, slots, resolved) = await ResolveMixAsync(parent, mix, goal, tainted, preview, cancellationToken).ConfigureAwait(false);

        return new ChildPlan(parentId, goal, [.. functions.Select(tool => tool.Name)], dropped, budget, slots,
            ChildSessions.Stricter(request.ApprovalMode ?? parent.ApprovalMode, parent.ApprovalMode), tainted, parent.Depth + 1)
        {
            Mix = resolved.IsEmpty ? null : resolved,
            OutputSchema = request.OutputSchema?.Clone(),
            By = request.By,
            CallId = request.CallId,
            Configurations = configurations,
            Functions = functions,
        };

        static int Clamp(int? asked, int fallback, int ceiling) => Math.Clamp(asked ?? fallback, 1, Math.Max(1, ceiling));
    }

    /// <summary>
    /// A5: starts the child a plan describes, without running it: a new session (<c>child-…</c>) whose log begins with
    /// <c>ChildOf</c>, under its own driver lock, with the plan's slots, toolset, budget and approval ceiling; its approval
    /// prompts go to whatever answers the parent's; the parent's log gets <c>ChildSpawned</c> and <see cref="ChildChanged"/> is
    /// raised. Run it with <see cref="RunChildAsync(CorvidSession, CancellationToken)"/>, which lets it go again; a child
    /// opened and never run holds one of the parent's <c>Corvid:Agents:MaxConcurrent</c> places until the runtime ends.
    /// </summary>
    /// <exception cref="ChildRefusedException">The parent is not open here, or already runs as many children as it may.</exception>
    public CorvidSession OpenChildSession(ChildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!TryGetSession(plan.ParentId, out var parent))
            throw new ChildRefusedException($"Session {TerminalText.Visible(plan.ParentId)} is not open in this process.");
        lock (_childGate)
        {
            var running = _childCounts.GetValueOrDefault(plan.ParentId);
            if (running >= Options.Agents.MaxConcurrent)
                throw new ChildRefusedException($"The session already runs {running} children, as many as Corvid:Agents:MaxConcurrent allows.");
            _childCounts[plan.ParentId] = running + 1;
        }
        var id = NewSessionId(ChildSessions.Surface);
        CorvidSession child;
        ChildStarted started;
        try
        {
            JsonElement? mix = plan.Mix is { } resolved ? Json(resolved.ToString()) : null;
            Log.Append(id, 0, [EventData.Create(new ChildOf(plan.ParentId, plan.Depth, plan.Goal, plan.Tools, plan.Budget, mix))]);
            var spawned = new ChildSpawned(id, plan.Goal, plan.Tools, plan.Budget, plan.By, plan.Tainted, mix, plan.OutputSchema, plan.CallId);
            var sequence = AppendTo(plan.ParentId, spawned);
            _childPlans[id] = plan;
            _prompters.SetParent(id, plan.ParentId);
            child = OpenChild(id, new SessionSettings(Interactive: parent.Interactive));
            // Its usage from the start, so StopChild finds a child that is listed but not run yet; its run then stops at once.
            _childUsage[id] = new ChildUsage(plan.Budget.MaxTokens, child.Cancel);
            started = new ChildStarted(spawned, sequence) { At = DateTimeOffset.UtcNow };
        }
        catch
        {
            ForgetChild(id, plan.ParentId);
            throw;
        }
        // M1 (A5 review): outside the try, so a surface's handler that throws never undoes an opened child.
        RaiseChildChanged(plan.ParentId, started);
        return child;
    }

    /// <summary>
    /// Raises <see cref="ChildChanged"/>; a handler that throws is reported as a notice (<see cref="Notice"/>) and never
    /// reaches the child's run or the call's other children.
    /// </summary>
    private void RaiseChildChanged(string parentId, SessionStreamUpdate update)
    {
        if (ChildChanged is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string, SessionStreamUpdate>>())
        {
            try
            {
                handler(parentId, update);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                ReportChild($"a surface failed to hear of a child of {TerminalText.Visible(parentId)}: {exception.Message}");
            }
        }
    }

    private void ReportChild(string reason)
    {
        var line = "child sessions: " + reason;
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

    /// <summary>A5: plans, opens and runs a child (<see cref="PlanChildAsync"/>, <see cref="OpenChildSession"/>, then the run).</summary>
    /// <exception cref="ChildRefusedException">The child could not be started; nothing was logged.</exception>
    public async Task<ChildResult> RunChildAsync(string parentId, ChildRequest request, CancellationToken cancellationToken = default)
    {
        var plan = await PlanChildAsync(parentId, request, cancellationToken).ConfigureAwait(false);
        return await RunChildAsync(OpenChildSession(plan), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A5: runs an opened child's goal as its one turn, within its budget: its steps (the turn's own budget), its tokens (the
    /// generator calls' reported use; the turn is cancelled once past it) and its wall time (cancelled when it runs out, on
    /// <see cref="ChildClock"/>). A cancellation of <paramref name="cancellationToken"/> (the parent's turn was cancelled)
    /// cancels the child. The goal is posted as outside content when the parent was tainted, so the child is tainted too
    /// (plan §10.3). With an output schema the answer is read as JSON and checked. Then <c>ChildFinished</c> is logged on the
    /// parent, <see cref="ChildChanged"/> raised, and the child let go of (its driver lock and actor). The kernel's
    /// <c>stuck</c> question is the stall monitor: a child that stalls ends <see cref="ChildStatuses.Stuck"/>.
    /// </summary>
    public async Task<ChildResult> RunChildAsync(CorvidSession child, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(child);
        var plan = child.Child ?? throw new ArgumentException("The session is not a child session.", nameof(child));
        // Made when the child was opened (so a listed child can be stopped before it runs); a fresh one otherwise.
        var usage = _childUsage.GetOrAdd(child.Id, _ => new ChildUsage(plan.Budget.MaxTokens, child.Cancel));
        using var watch = Feed.Subscribe(child.Id, usage);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(plan.Budget.MaxSeconds), ChildClock);
        var timedOut = timeout.Token.Register(() =>
        {
            usage.TimedOut = true;
            usage.Stop();
        });
        // A5: a stop asked for before the child's turn has started is repeated once it starts (ChildUsage), so none is missed.
        using var cancelled = cancellationToken.Register(usage.Stop);
        Delivery? delivery = null;
        Exception? failure = null;
        var abandoned = false;
        var message = plan.Goal;
        var earlierSteps = 0;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                // Stopped while it waited to run (StopChild on a listed child): it never starts.
                if (attempt == 0 && usage.Stopping)
                    break;
                try
                {
                    // A tainted child stays tainted: Corvid's repair line is posted as outside content too, never as the person.
                    var origin = plan.Tainted || child.Tainted ? EnvelopeOrigin.External : EnvelopeOrigin.User;
                    var posted = await child.PostAsync(message, origin, CancellationToken.None).ConfigureAwait(false);
                    (delivery, abandoned) = await DeliveryOrStopAsync(posted, usage).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failure = exception;
                }
                // Plan §14, "validated on return": an answer outside the schema gets one repair turn within the same budget.
                // The repair turn gets only what the first left of the step budget (TurnRunner.StepLimit), so the whole run stays
                // within the steps the person approved; with none left there is no repair and the answer is invalid output.
                if (attempt == 0 && failure is null && !abandoned && plan.OutputSchema is { } schema && !usage.OverBudget && !usage.TimedOut
                    && !cancellationToken.IsCancellationRequested && !usage.Stopping
                    && delivery?.Result is { Status: TurnStatus.Answered } first && first.Steps < plan.Budget.MaxSteps
                    && OutputSchema.Validate(schema, first.Text, out _) is { Count: > 0 } errors)
                {
                    earlierSteps = first.Steps;
                    child.Runner.StepLimit = plan.Budget.MaxSteps - first.Steps;
                    message = RepairMessage(errors);
                    continue;
                }
                break;
            }
        }
        finally
        {
            // L2 (A5 review): the wall-time budget ends with the delivery, so an answer that came in time stays answered.
            timedOut.Dispose();
        }
        ChildDelivered?.Invoke(child.Id);

        var result = Finish(child, plan, delivery, failure, usage, cancellationToken.IsCancellationRequested || (delivery is null && failure is null && usage.Stopping));
        if (earlierSteps > 0)
            result = result with { Steps = result.Steps + earlierSteps };
        if (_stoppedChildren.TryRemove(child.Id, out _) && result.Status == ChildStatuses.Cancelled)
            result = result with { Detail = "stopped by the person" };
        if (abandoned)
            result = result with { Detail = Join(result.Detail, "its turn had not stopped; it is let go of once it does") };
        // L3 (A5 review): a provider that reports no usage is counted with the token counter's estimate, and the result says so.
        if (usage.Estimated)
            result = result with { TokensEstimated = true, Detail = Join(result.Detail, "tokens estimated: the model reported no usage") };
        ChildEnded? ended = null;
        try
        {
            var finished = new ChildFinished(child.Id, result.Status, result.Steps, result.Tokens, result.Tainted,
                result.Text is { Length: > MaxChildTextChars } whole ? whole[..MaxChildTextChars] + "…" : result.Text, result.Detail,
                plan.OutputSchema is null ? null : result.Status != ChildStatuses.InvalidOutput && result.Output is not null);
            var sequence = AppendTo(plan.ParentId, finished);
            ended = new ChildEnded(finished, sequence) { At = DateTimeOffset.UtcNow };
        }
        finally
        {
            ForgetChild(child.Id, plan.ParentId);
            await LetGoAsync(child, abandoned).ConfigureAwait(false);
        }
        // M1 (A5 review): a handler that throws never fails the call or loses the other children's results.
        RaiseChildChanged(plan.ParentId, ended!);
        return result;

        static string Join(string? detail, string note) => detail is { Length: > 0 } ? detail + "; " + note : note;
    }

    /// <summary>
    /// M2 (A5 review): how long a stopped child's turn (stopped by the person, the parent, its budget or its time) is waited for
    /// before its run ends without it: a tool that ignores cancellation must not hold the parent's call. Tests shorten it.
    /// </summary>
    internal TimeSpan ChildStopGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a finished child's actor is waited for to settle before its release is left to run when it does.</summary>
    internal TimeSpan ChildSettle { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A test hook: a child's delivery is in (or was given up on), before its result is made.</summary>
    internal Action<string>? ChildDelivered { get; set; }

    // M2 (A5 review): children whose turn outlived their run, let go of once their actor is idle; awaited at the runtime's end.
    private readonly ConcurrentDictionary<string, Task> _childReleases = new(StringComparer.Ordinal);

    /// <summary>The child's delivery, or null and true when a stop came and its turn did not end within <see cref="ChildStopGrace"/>.</summary>
    private async Task<(Delivery? Delivery, bool Abandoned)> DeliveryOrStopAsync(Task<Delivery> posted, ChildUsage usage)
    {
        if (await Task.WhenAny(posted, usage.Stopped).ConfigureAwait(false) != posted)
        {
            try
            {
                await posted.WaitAsync(ChildStopGrace, ChildClock).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return (null, true);
            }
        }
        return (await posted.ConfigureAwait(false), false);
    }

    /// <summary>
    /// Lets a finished child go (its driver lock and actor): at once when its actor settles within <see cref="ChildSettle"/>,
    /// else (or when its turn was given up on) once it goes idle, tracked so the runtime's end waits for it.
    /// </summary>
    private async Task LetGoAsync(CorvidSession child, bool abandoned)
    {
        // The delivery comes before the actor marks the turn finished: let it settle, then let the child go.
        var idle = child.Actor.IdleAsync(_lifetime.Token);
        if (!abandoned)
            await idle.WaitAsync(ChildSettle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (idle.IsCompletedSuccessfully && await ReleaseDriverAsync(child.Id).ConfigureAwait(false))
            return;
        if (_lifetime.IsCancellationRequested)
            return;
        var release = ReleaseWhenIdleAsync(child.Id, idle);
        _childReleases[child.Id] = release;
        _ = release.ContinueWith(_ => _childReleases.TryRemove(KeyValuePair.Create(child.Id, release)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task ReleaseWhenIdleAsync(string childId, Task idle)
    {
        await Task.Yield();
        // Idle again after each try: a release refused while the actor was still busy tries once more when it settles.
        for (var tries = 0; tries < 8 && !_lifetime.IsCancellationRequested; tries++)
        {
            await idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (!idle.IsCompletedSuccessfully || await ReleaseDriverAsync(childId).ConfigureAwait(false))
                return;
            if (!TryGetSession(childId, out var session))
                return;
            idle = session.Actor.IdleAsync(_lifetime.Token);
        }
    }

    /// <summary>The children's releases still waiting on their actors (tests read it).</summary>
    internal Task ChildReleasesIdleAsync() => Task.WhenAll(_childReleases.Values.ToArray());

    private static ChildResult Finish(CorvidSession child, ChildPlan plan, Delivery? delivery, Exception? failure, ChildUsage usage,
        bool parentCancelled)
    {
        var turn = delivery?.Result;
        var steps = turn?.Steps ?? 0;
        var tainted = turn?.Tainted ?? child.Tainted;
        if (usage.OverBudget)
            return new(child.Id, ChildStatuses.TokenBudgetExhausted, turn?.Text, null, steps, usage.Tokens, tainted,
                $"used {usage.Tokens} tokens of {plan.Budget.MaxTokens}");
        // L2 (A5 review): an answer that came before the timer fired is the child's answer, not a time-out.
        if (usage.TimedOut && turn?.Status != TurnStatus.Answered)
            return new(child.Id, ChildStatuses.TimedOut, turn?.Text, null, steps, usage.Tokens, tainted, $"ran past {plan.Budget.MaxSeconds} s");
        if (turn is null)
            return parentCancelled
                ? new(child.Id, ChildStatuses.Cancelled, null, null, steps, usage.Tokens, tainted, "the parent's turn was cancelled")
                : new(child.Id, ChildStatuses.Failed, null, null, steps, usage.Tokens, tainted,
                    (failure ?? delivery?.Error)?.Message ?? "the child's turn did not finish");
        var status = turn.Status switch
        {
            TurnStatus.Answered => ChildStatuses.Answered,
            TurnStatus.Stuck => ChildStatuses.Stuck,
            TurnStatus.StepBudgetExhausted => ChildStatuses.StepBudgetExhausted,
            TurnStatus.Cancelled => ChildStatuses.Cancelled,
            // A child never asks (TurnOptions.AsksQuestions), so a clarifying end is not one it can have; it counts as failed.
            _ => ChildStatuses.Failed,
        };
        var detail = status switch
        {
            ChildStatuses.StepBudgetExhausted => $"took its {plan.Budget.MaxSteps} steps",
            ChildStatuses.Stuck => "the stall monitor (stuck) stopped it",
            ChildStatuses.Cancelled => parentCancelled ? "the parent's turn was cancelled" : "cancelled",
            ChildStatuses.Failed => turn.Text,
            _ => null,
        };
        if (status == ChildStatuses.Answered && plan.OutputSchema is { } schema)
        {
            var errors = OutputSchema.Validate(schema, turn.Text, out var output);
            return errors.Count == 0
                ? new(child.Id, status, turn.Text, output, steps, usage.Tokens, tainted, null)
                : new(child.Id, ChildStatuses.InvalidOutput, turn.Text, output, steps, usage.Tokens, tainted,
                    "the answer does not meet the output schema: " + string.Join("; ", errors));
        }
        return new(child.Id, status, turn.Text, null, steps, usage.Tokens, tainted, detail);
    }

    /// <summary>
    /// H1 (A5 review): lets go of a child opened by a call that then failed before running it: <c>ChildFinished</c> (cancelled)
    /// on the parent, its place, plan, usage, prompter route, driver lock and actor, and the surfaces told. Never throws.
    /// </summary>
    private async Task AbandonChildAsync(CorvidSession child, string why)
    {
        var plan = child.Child!;
        ChildEnded? ended = null;
        try
        {
            var finished = new ChildFinished(child.Id, ChildStatuses.Cancelled, 0, 0, plan.Tainted, null, why, null);
            ended = new ChildEnded(finished, AppendTo(plan.ParentId, finished)) { At = DateTimeOffset.UtcNow };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportChild($"could not log the end of {child.Id}: {exception.Message}");
        }
        _stoppedChildren.TryRemove(child.Id, out _);
        ForgetChild(child.Id, plan.ParentId);
        try
        {
            await LetGoAsync(child, abandoned: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ReportChild($"could not let go of {child.Id}: {exception.Message}");
        }
        if (ended is not null)
            RaiseChildChanged(plan.ParentId, ended);
    }

    private void ForgetChild(string childId, string parentId)
    {
        _childPlans.TryRemove(childId, out _);
        _childUsage.TryRemove(childId, out _);
        // A stop that raced the child's end (StopChild between its check and its write) leaves nothing behind.
        _stoppedChildren.TryRemove(childId, out _);
        _prompters.Forget(childId);
        lock (_childGate)
        {
            if (_childCounts.TryGetValue(parentId, out var running))
            {
                if (running <= 1)
                    _childCounts.Remove(parentId);
                else
                    _childCounts[parentId] = running - 1;
            }
        }
    }

    // Appends one event to a session this process drives, retrying a head conflict with the turn's own appends.
    private long AppendTo(string sessionId, SessionEvent payload)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Log.Append(sessionId, Log.GetHead(sessionId), [EventData.Create(payload)])[0].Sequence;
            }
            catch (EventLogConflictException) when (attempt < 8)
            {
            }
        }
    }

    /// <summary>A child's toolset: each tool asked for that the parent has and a child may use, the rest with why not.</summary>
    private static (ImmutableArray<ToolFunction> Kept, ImmutableArray<DroppedTool> Dropped) ChildToolset(CorvidSession parent, ChildRequest request)
    {
        var kept = ImmutableArray.CreateBuilder<ToolFunction>();
        var dropped = ImmutableArray.CreateBuilder<DroppedTool>();
        foreach (var name in (request.Tools.IsDefault ? [] : request.Tools).Distinct(StringComparer.Ordinal))
        {
            var shown = TerminalText.Visible(name ?? "");
            if (string.IsNullOrWhiteSpace(name) || !parent.Tools.TryGet(name, out var tool))
            {
                dropped.Add(new(shown, "the parent session has no such tool"));
                continue;
            }
            var effects = tool.Descriptor.Risk.SideEffects;
            var reason = request.OfferedTools is { } offered && !offered.Contains(name) ? "it was not offered to the calling turn"
                : tool is AskUserTool || name == AskUserTool.ToolName ? "a child cannot ask the person anything"
                : name == ChildSessions.DelegateToolName ? "a child cannot start children of its own"
                : effects.HasFlag(SideEffects.Memory) ? "a child cannot write memory"
                : effects.HasFlag(SideEffects.Messaging) ? "a child cannot send messages"
                : effects.HasFlag(SideEffects.Schedule) || name.StartsWith("cron_", StringComparison.Ordinal) ? "a child cannot schedule jobs"
                : null;
            if (reason is null)
                kept.Add(tool);
            else
                dropped.Add(new(shown, reason));
        }
        return (kept.ToImmutable(), dropped.ToImmutable());
    }

    private static ToolRegistry ChildTools(ChildPlan plan)
    {
        var registry = new ToolRegistry();
        registry.AddRange(plan.Functions);
        return registry;
    }

    /// <summary>
    /// A child's fixed system text, one of the egress gate's trusted texts (<see cref="ModelComposer.Egress"/>): Corvid's own
    /// words, never read as PII nor rewritten for a gated child model. The output schema after it is the model's and is read.
    /// </summary>
    internal const string ChildSystemPrompt =
        "You are a child session working on one goal for another Corvid session. Nobody can answer questions: do the work with " +
        "the tools you have and answer with the result.";

    internal const string ChildSchemaPrompt = " Answer with only a JSON object that meets this schema: ";

    private static string ChildPrompt(ChildPlan plan) =>
        ChildSystemPrompt + (plan.OutputSchema is { } schema ? ChildSchemaPrompt + schema.GetRawText() : "");

    /// <summary>
    /// The child's slots: those the mix names, each checked and bound at subagent priority with its ladder and fallbacks, and
    /// the parent's session defaults and ladders for the rest, rebound at subagent priority. Also the view for the card, and
    /// the mix with catalogue names as <c>ChildSpawned</c> records it.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, SlotConfiguration> Configurations, ImmutableArray<ChildSlot> Slots, ModelMix Resolved)>
        ResolveMixAsync(CorvidSession parent, ModelMix mix, string goal, bool tainted, bool preview, CancellationToken cancellationToken)
    {
        var configurations = new Dictionary<string, SlotConfiguration>(StringComparer.Ordinal);
        var view = ImmutableArray.CreateBuilder<ChildSlot>();
        var resolved = new List<KeyValuePair<string, ModelMixSlot>>();
        foreach (var (slot, entry) in mix.Slots)
        {
            var name = ResolveModel(slot, entry.Model);
            var primary = preview ? null : BindChild(slot, name);
            var primaryProfile = primary?.Profile ?? Catalogue.Find(name).Profile;
            if (await GateRefusalAsync(primaryProfile, goal, tainted, cancellationToken).ConfigureAwait(false) is { } refusal)
                throw new ChildRefusedException($"The privacy gate would refuse {TerminalText.Visible(name)} for the {slot}" +
                    (tainted ? " in a session holding outside content" : "") + $": {refusal}");
            var inherited = parent.Slots.TryConfiguration(slot, out var parentConfiguration) ? parentConfiguration : null;
            var ladder = !entry.Escalate ? []
                : entry.Ladder is { } given ? given.Select(step => ResolveModel(slot, step)).ToList()
                : [.. (inherited?.Escalation ?? []).Select(_composer.NameOf).OfType<string>().Where(step => !Same(step, name) && AllowedForChildren(step)
                    && Catalogue.Check(step).IsReady)];
            var fallbacks = entry.Fallbacks is { } chain ? chain.Select(step => ResolveModel(slot, step)).ToList()
                : [.. (inherited?.Fallbacks ?? []).Select(_composer.NameOf).OfType<string>().Where(step => !Same(step, name) && AllowedForChildren(step)
                    && Catalogue.Check(step).IsReady)];
            var ladderBindings = await GatedAsync(slot, ladder, goal, tainted, cancellationToken).ConfigureAwait(false);
            var fallbackBindings = await GatedAsync(slot, fallbacks, goal, tainted, cancellationToken).ConfigureAwait(false);
            if (primary is not null)
                configurations[slot] = new SlotConfiguration(slot, primary, [.. ladderBindings.Select(pair => pair.Binding!)],
                    [.. fallbackBindings.Select(pair => pair.Binding!)]);
            ImmutableArray<string> ladderNames = [.. ladderBindings.Select(pair => pair.Name)];
            ImmutableArray<string>? fallbackNames = entry.Fallbacks is null ? null : [.. fallbackBindings.Select(pair => pair.Name)];
            view.Add(new ChildSlot(slot, name, primaryProfile.Privacy, ladderNames, entry.Escalate));
            resolved.Add(KeyValuePair.Create(slot, new ModelMixSlot(name, ladderNames, fallbackNames, entry.Escalate)));
        }

        // The rest run on the parent's choices. The decider stays the registry's (the shared fabric) unless the parent chose one.
        var named = mix.Slots.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var slot in Registry.BoundSlots.Concat(parent.Slots.OwnSlots).Concat(ModelSlots.All.Where(parent.Slots.HasSessionChoice))
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (named.Contains(slot))
                continue;
            if (slot == ModelSlots.Decider && !parent.Slots.HasSessionChoice(slot) && !parent.Slots.OwnSlots.Contains(slot))
                continue;
            var current = parent.Slots.SessionDefault(slot);
            var configuration = parent.Slots.TryConfiguration(slot, out var found) ? found : new SlotConfiguration(slot, current, [], []);
            // Security L4 (A5 review): Corvid:Agents:Models narrows the models a child is given, the inherited ones too, in the
            // slots a remote model can serve (generator, summarizer, vision): those are the ones that can send the child's work
            // off the machine. The extractor and the embedders are in-process, and the decider is the parent's own judge of the
            // child (security M1), so a list naming only generators does not refuse every child.
            ImmutableArray<ModelBinding> escalation = configuration.Escalation, fallbacks = configuration.Fallbacks;
            if (slot is ModelSlots.Generator or ModelSlots.Summarizer or ModelSlots.Vision && Options.Agents.Models.Count > 0)
            {
                var inheritedName = _composer.NameOf(current) ?? current.Profile.Id;
                if (!AllowedForChildren(inheritedName))
                    throw new ChildRefusedException($"The child would run the {slot} on the parent's {TerminalText.Visible(inheritedName)}, which is not in " +
                        $"Corvid:Agents:Models; name an allowed model for the {slot} in the mix, or add it to Corvid:Agents:Models.");
                escalation = [.. escalation.Where(step => AllowedForChildren(_composer.NameOf(step) ?? step.Profile.Id))];
                fallbacks = [.. fallbacks.Where(step => AllowedForChildren(_composer.NameOf(step) ?? step.Profile.Id))];
            }
            if (!preview)
                configurations[slot] = new SlotConfiguration(slot, Rebind(slot, current), [.. escalation.Select(step => Rebind(slot, step))],
                [.. fallbacks.Select(step => Rebind(slot, step))]);
            view.Add(new ChildSlot(slot, _composer.NameOf(current) ?? current.Profile.Id, current.Profile.Privacy,
                [.. escalation.Select(step => _composer.NameOf(step) ?? step.Profile.Id)], !escalation.IsEmpty));
        }
        return (configurations, view.ToImmutable(), resolved.Count == 0 ? ModelMix.Empty : ModelMix.Create(resolved));

        async Task<List<(string Name, ModelBinding? Binding)>> GatedAsync(string slot, IEnumerable<string> names, string text, bool taint,
            CancellationToken token)
        {
            var kept = new List<(string, ModelBinding?)>();
            foreach (var step in names)
            {
                // A preview binds nothing: the catalogue's profile is what the gate reads.
                var binding = preview ? null : BindChild(slot, step);
                // A step the gate would refuse is left out: escalation and fallback then never reach it.
                if (await GateRefusalAsync(binding?.Profile ?? Catalogue.Find(step).Profile, text, taint, token).ConfigureAwait(false) is null)
                    kept.Add((step, binding));
            }
            return kept;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // A model the mix names, as the catalogue names it: known, serving the slot, allowed for children and usable now.
    private string ResolveModel(string slot, string model)
    {
        if (!Catalogue.TryFind(model, out var descriptor))
            throw new ChildRefusedException($"There is no model named \"{TerminalText.Visible(model)}\" in the catalogue.");
        var name = TerminalText.Visible(descriptor.Name);
        if (!descriptor.Serves(slot))
            throw new ChildRefusedException($"{name} cannot serve as the {slot}; it serves {string.Join(", ", descriptor.Slots)}.");
        if (!AllowedForChildren(descriptor.Name))
            throw new ChildRefusedException($"{name} is not in Corvid:Agents:Models, so a child cannot be given it.");
        var availability = Catalogue.Check(descriptor.Name);
        if (!availability.IsReady)
            throw new ChildRefusedException($"{name} is not usable now: {TerminalText.Visible(availability.Detail)}");
        return descriptor.Name;
    }

    private ModelBinding BindChild(string slot, string model)
    {
        try
        {
            return _composer.Bind(slot, model, DevicePriority.Subagent);
        }
        catch (Exception exception) when (exception is ModelUnavailableException or ArgumentException or InvalidOperationException)
        {
            throw new ChildRefusedException($"{TerminalText.Visible(model)} did not load for the {slot}: {TerminalText.Visible(exception.Message)}");
        }
    }

    // A binding of the parent's at subagent priority (its catalogue name rebound); the decider and bindings made elsewhere as they are.
    // H1 (A5 review): a binding that fails to load refuses the child, as a mix's own does (BindChild), and is never an escape.
    private ModelBinding Rebind(string slot, ModelBinding binding) =>
        slot != ModelSlots.Decider && _composer.NameOf(binding) is { } name ? BindChild(slot, name) : binding;

    // The egress gate (plan §10.4) on what the child would send first, its goal, with the parent's taint: null when it would go.
    private async Task<string?> GateRefusalAsync(ModelProfile profile, string goal, bool tainted, CancellationToken cancellationToken)
    {
        if (Egress.PolicyFor(profile) is null)
            return null;
        var decision = await Egress.AssessAsync(profile, [goal], tainted, cancellationToken).ConfigureAwait(false);
        return decision.Refused ? decision.Reason : null;
    }

    /// <summary>Counts a child's generator tokens as providers report them, and cancels its turn once past the budget.</summary>
    private sealed class ChildUsage(int budget, Action cancel) : ITurnObserver
    {
        private long _tokens;
        private int _over;

        public long Tokens => Interlocked.Read(ref _tokens);

        public bool OverBudget => Volatile.Read(ref _over) == 1;

        public volatile bool TimedOut;

        private int _stop;

        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Cancels the child's turn now and again at its next sign of life, for a stop that came before it started.</summary>
        public void Stop()
        {
            Interlocked.Exchange(ref _stop, 1);
            cancel();
            _stopped.TrySetResult();
        }

        /// <summary>Completes once a stop is asked for (the person, the parent, the budget or the clock).</summary>
        public Task Stopped => _stopped.Task;

        private int _estimated;

        /// <summary>Some of <see cref="Tokens"/> is the token counter's estimate: a provider reported no usage.</summary>
        public bool Estimated => Volatile.Read(ref _estimated) == 1;

        public bool Stopping => Volatile.Read(ref _stop) == 1;

        private void Again()
        {
            if (Stopping)
                cancel();
        }

        public void GenerationStarted() => Again();

        public void Decided(CheckpointDecisions decisions) => Again();

        public void Progress(int step, int maxSteps) => Again();

        public void ToolStarted(string callId, string tool, bool approval) => Again();

        public void UsageEstimated(long inputTokens, long outputTokens)
        {
            Volatile.Write(ref _estimated, 1);
            Usage(inputTokens, outputTokens);
        }

        public void Usage(long? inputTokens, long? outputTokens)
        {
            var total = Interlocked.Add(ref _tokens, (inputTokens ?? 0) + (outputTokens ?? 0));
            if (total > budget && Interlocked.Exchange(ref _over, 1) == 0)
                cancel();
            Again();
        }
    }
}
