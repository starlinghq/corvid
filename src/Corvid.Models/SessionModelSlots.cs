using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Models;

/// <summary>
/// One session's current slot bindings and the switch procedure of plan §4.2. Every change of binding is a
/// <c>ModelSwitched</c> event in the session log, appended before the new binding takes effect.
/// </summary>
/// <remarks>
/// <para>
/// Starting bindings come from the registry, or, for the slots the session's own mix configures (A5, a child session's),
/// from that mix; both are configuration, not events, so a session's projected <c>ModelProfiles</c> list only the slots
/// that have switched. Switches are serialized per session.
/// </para>
/// <para>
/// A5, the session default: a slot returns to its session default when a turn ends (de-escalation, plan §6). That is the
/// model the person chose for the session (<see cref="SetSessionDefaultAsync"/>, logged with the reason <c>session</c>),
/// else the session's own mix's default, else the registry's default as it is then, so a session that set nothing follows a
/// registry rebinding as before. Escalation and fallback still move off it. A pin is separate: it keeps the slot where it is
/// when the turn ends. A <see cref="SwitchReason.User"/> switch through <see cref="SwitchAsync(string, ModelBinding, SwitchReason, bool, CancellationToken)"/>
/// lasts for the turn only.
/// </para>
/// </remarks>
public sealed class SessionModelSlots
{
    private const int MaxAppendAttempts = 8;
    private const int ReadPage = 256;

    private readonly ModelSlotRegistry _registry;
    private readonly IEventLog _log;
    private readonly ModelSwitchOptions _options;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly Lock _state = new();
    private readonly Dictionary<string, ModelBinding> _current = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _parked = new(StringComparer.Ordinal);
    // A5: the session's own slot configurations (a mix) over the registry's, and the person's session choices over both.
    private readonly ImmutableDictionary<string, SlotConfiguration> _own;
    private readonly Dictionary<string, ModelBinding> _chosen = new(StringComparer.Ordinal);

    /// <param name="slots">
    /// The session's own configuration per slot (A5: a child session's model mix, resolved): its default, escalation ladder
    /// and fallbacks replace the registry's for those slots. Other slots follow the registry.
    /// </param>
    public SessionModelSlots(ModelSlotRegistry registry, IEventLog log, string sessionId, ModelSwitchOptions? options = null,
        IReadOnlyDictionary<string, SlotConfiguration>? slots = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _registry = registry;
        _log = log;
        _options = options ?? new ModelSwitchOptions();
        SessionId = sessionId;
        var own = ImmutableDictionary.CreateBuilder<string, SlotConfiguration>(StringComparer.Ordinal);
        foreach (var (slot, configuration) in slots ?? ImmutableDictionary<string, SlotConfiguration>.Empty)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (!string.Equals(configuration.Slot, slot, StringComparison.Ordinal))
                throw new ArgumentException($"The configuration for '{slot}' is for '{configuration.Slot}'.", nameof(slots));
            ModelSlots.Check(slot, configuration.Default);
            foreach (var binding in configuration.Escalation.Concat(configuration.Fallbacks))
                ModelSlots.Check(slot, binding);
            own[slot] = configuration;
        }
        _own = own.ToImmutable();
        // Capture the defaults now, so rebinding the registry later cannot silently change this session's model.
        foreach (var slot in registry.BoundSlots)
            _current[slot] = registry.Get(slot).Default;
        foreach (var (slot, configuration) in _own)
            _current[slot] = configuration.Default;
    }

    public string SessionId { get; }

    public ModelBinding Current(string slot)
    {
        lock (_state)
        {
            if (!_current.TryGetValue(slot, out var binding))
                _current[slot] = binding = Configuration(slot).Default;
            return binding;
        }
    }

    /// <summary>The slot's current binding, or false when neither the session nor the registry binds the slot.</summary>
    public bool TryCurrent(string slot, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ModelBinding? binding)
    {
        lock (_state)
        {
            if (!_current.TryGetValue(slot, out binding) && TryConfiguration(slot, out var configuration))
                _current[slot] = binding = configuration.Default;
            return binding is not null;
        }
    }

    /// <summary>
    /// The slot's configuration for this session (A5): its own mix's when it has one for the slot, else the registry's as it
    /// is now. Its <see cref="SlotConfiguration.Default"/> is the mix's or the registry's, not the person's choice (see
    /// <see cref="SessionDefault"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither binds the slot.</exception>
    public SlotConfiguration Configuration(string slot) =>
        TryConfiguration(slot, out var configuration) ? configuration : _registry.Get(slot);

    /// <summary>The slot's configuration for this session, or false when neither its mix nor the registry binds it.</summary>
    public bool TryConfiguration(string slot, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SlotConfiguration? configuration)
    {
        if (_own.TryGetValue(slot, out configuration))
            return true;
        if (_registry.TryGet(slot, out var registered))
        {
            configuration = registered;
            return true;
        }
        configuration = null;
        return false;
    }

    /// <summary>The slots this session's own mix configures (A5); empty for a session that follows the registry.</summary>
    public IReadOnlyCollection<string> OwnSlots => _own.Keys.ToArray();

    /// <summary>
    /// Where the slot returns when a turn ends (A5): the person's choice for the session when they made one, else
    /// <see cref="Configuration"/>'s default (the mix's, or the registry's as it is now).
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing binds the slot.</exception>
    public ModelBinding SessionDefault(string slot)
    {
        lock (_state)
            if (_chosen.TryGetValue(slot, out var chosen))
                return chosen;
        return Configuration(slot).Default;
    }

    /// <summary>
    /// A5: puts back a choice the person made for the session before it was opened again (a resumed session, a fork): the
    /// slot uses <paramref name="binding"/> and returns to it when a turn ends, as after <see cref="SetSessionDefaultAsync"/>.
    /// With <paramref name="pinned"/> it is pinned again. Nothing is logged (the log already holds the choice's
    /// <c>ModelSwitched</c>) and no one is told. It sets the slot's binding whatever it is now, so call it as the session
    /// opens, before anything switches the slot.
    /// </summary>
    public void RestoreSessionDefault(string slot, ModelBinding binding, bool pinned = false)
    {
        ModelSlots.Check(slot, binding);
        lock (_state)
        {
            _chosen[slot] = binding;
            _current[slot] = binding;
            if (pinned)
                _pinned.Add(slot);
            else
                _pinned.Remove(slot);
        }
    }

    /// <summary>Whether the person chose the slot's model for this session (<see cref="SetSessionDefaultAsync"/>).</summary>
    public bool HasSessionChoice(string slot)
    {
        lock (_state)
            return _chosen.ContainsKey(slot);
    }

    public bool IsPinned(string slot)
    {
        lock (_state)
            return _pinned.Contains(slot);
    }

    /// <summary>
    /// Unpins the slot, so the turn's end returns it to its session default again (a pin only stops de-escalation: escalation
    /// and fallback move a pinned slot too). A5 review: logged as a <c>ModelSwitched</c> from the slot's model to itself with
    /// the reason <see cref="SwitchReasons.Unpin"/>, so a session opened again does not pin it again; like a switch it waits
    /// for running tool calls. False when the slot was not pinned (nothing is logged).
    /// </summary>
    public Task<bool> UnpinAsync(string slot, CancellationToken cancellationToken = default) =>
        SerializedAsync(waitForTools: true, () =>
        {
            if (!IsPinned(slot))
                return Task.FromResult(false);
            var profile = TryCurrent(slot, out var bound) ? bound.Profile : ModelProfile.Unbound;
            AppendInPlace(slot, profile, SwitchReasons.Unpin, pinned: false, reset: null, cancellationToken);
            SetPinned(slot, false);
            return Task.FromResult(true);
        }, cancellationToken);

    /// <summary>A client that always calls the slot's current binding and falls back along the slot's chain.</summary>
    public IChatClient GetChatClient(string slot, Func<Exception, bool>? isFallbackError = null)
    {
        if (ModelSlots.ContractOf(slot) != typeof(IChatClient))
            throw new ArgumentException($"Slot '{slot}' is not a chat slot.", nameof(slot));
        return new SlotChatClient(this, slot, isFallbackError ?? FallbackErrors.IsProviderFailure);
    }

    /// <summary>
    /// Marks an issued tool call as running. A switch waits until every lease is disposed. Append the call's
    /// <c>ToolResult</c> before disposing the lease: a switch refuses to follow an issued call that has no result.
    /// </summary>
    public IDisposable TrackToolCall(string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        lock (_state)
        {
            if (_parked.Contains(callId) || !_inFlight.TryAdd(callId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
                throw new InvalidOperationException($"Tool call '{callId}' is already tracked or parked.");
        }
        return new ToolCallLease(this, callId);
    }

    /// <summary>
    /// Marks an issued tool call as waiting on an approval. It is not running, so a switch neither waits for it nor
    /// treats it as orphaned, and the result reports it. Dispose when the approval is resolved, then track the call if it runs.
    /// </summary>
    public IDisposable ParkToolCall(string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        lock (_state)
        {
            if (_inFlight.ContainsKey(callId) || !_parked.Add(callId))
                throw new InvalidOperationException($"Tool call '{callId}' is already tracked or parked.");
        }
        return new ParkedCallLease(this, callId);
    }

    /// <summary>
    /// An approved parked call starts running: it moves from parked to tracked in one step, so a concurrent switch never
    /// sees it as neither (and refuses it as orphaned). Dispose the returned lease after the call's result is logged; the
    /// park's own lease then does nothing.
    /// </summary>
    public IDisposable RunParkedToolCall(string callId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        lock (_state)
        {
            if (!_parked.Contains(callId) || _inFlight.ContainsKey(callId))
                throw new InvalidOperationException($"Tool call '{callId}' is not parked.");
            _parked.Remove(callId);
            _inFlight[callId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new ToolCallLease(this, callId);
    }

    /// <summary>
    /// Switches a slot. A <see cref="SwitchReason.User"/> switch lasts for the turn: its end returns the slot to the session
    /// default unless <paramref name="pin"/> pins it. <see cref="SwitchReason.Session"/> is <see cref="SetSessionDefaultAsync"/>.
    /// The person's own <c>/model</c> is sticky (A5): surfaces switch through the scoped overload or the session's
    /// <c>SetModelAsync</c>.
    /// </summary>
    public Task<SwitchResult> SwitchAsync(string slot, ModelBinding target, SwitchReason reason = SwitchReason.User,
        bool pin = false, CancellationToken cancellationToken = default)
    {
        ModelSlots.Check(slot, target);
        if (reason == SwitchReason.Session)
            return SetSessionDefaultAsync(slot, target, pin, cancellationToken);
        return SerializedAsync(waitForTools: reason != SwitchReason.Fallback,
            () => SwitchCoreAsync(slot, target, reason, pin, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The person's switch with how long it lasts (A5): <see cref="SwitchScope.Session"/> and <see cref="SwitchScope.Pin"/>
    /// through <see cref="SetSessionDefaultAsync"/> (unpinned and pinned), <see cref="SwitchScope.Turn"/> as a
    /// <see cref="SwitchReason.User"/> switch that the turn's end undoes.
    /// </summary>
    public Task<SwitchResult> SwitchAsync(string slot, ModelBinding target, SwitchScope scope, CancellationToken cancellationToken = default) =>
        scope switch
        {
            SwitchScope.Session => SetSessionDefaultAsync(slot, target, pin: false, cancellationToken),
            SwitchScope.Pin => SetSessionDefaultAsync(slot, target, pin: true, cancellationToken),
            SwitchScope.Turn => SwitchAsync(slot, target, SwitchReason.User, pin: false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

    /// <summary>
    /// The sticky "this session" switch (A5): <paramref name="target"/> becomes the slot's session default and the slot
    /// switches to it, logged as <c>ModelSwitched</c> with the reason <c>session</c>, once issued tool calls finish (a
    /// running turn takes it at its next step). Escalation and fallback still move off it, and de-escalation returns to it,
    /// until the person changes it again. <paramref name="pin"/> pins the slot, or unpins it. A refusal (the privacy gate)
    /// changes neither the binding nor the default. When the slot already uses the target, the default (or the pin) still
    /// changes, logged as a <c>session</c> switch from the model to itself so it lasts when the session is opened again, and
    /// the result is <see cref="SwitchStatus.Unchanged"/> with <see cref="SwitchResult.SessionDefaultChanged"/>. Every
    /// <c>session</c> switch logs its pin (<c>Pinned</c>).
    /// </summary>
    public Task<SwitchResult> SetSessionDefaultAsync(string slot, ModelBinding target, bool pin = false, CancellationToken cancellationToken = default)
    {
        ModelSlots.Check(slot, target);
        return SerializedAsync(waitForTools: true, () => SwitchCoreAsync(slot, target, SwitchReason.Session, pin, cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Forgets the person's choice for the slot (A5): its session default is its mix's or the registry's again, and an
    /// unpinned slot still on the choice switches back there now (logged as <c>de_escalation</c>). A5 review: the reset is
    /// logged either way (<c>ModelSwitched</c> with <c>Reset</c>; from the slot's model to itself when it stays where it is,
    /// pinned or off the choice), so a session opened again does not bring the choice back. Null when there was no choice.
    /// </summary>
    public Task<SwitchResult?> ClearSessionDefaultAsync(string slot, CancellationToken cancellationToken = default) =>
        SerializedAsync<SwitchResult?>(waitForTools: true, async () =>
        {
            ModelBinding? chosen;
            lock (_state)
            {
                if (!_chosen.TryGetValue(slot, out chosen))
                    return null;
            }
            // Nothing configures the slot (a decider chosen on the degraded tier): it goes back to none.
            var target = TryConfiguration(slot, out var configured) ? configured.Default : null;
            var current = TryCurrent(slot, out var bound) ? bound : null;
            if (IsPinned(slot) || !ReferenceEquals(current, chosen))
            {
                var profile = current?.Profile ?? ModelProfile.Unbound;
                var sequence = AppendInPlace(slot, profile, SwitchReason.DeEscalation.ToWireName(), pinned: null, reset: true, cancellationToken);
                lock (_state)
                    _chosen.Remove(slot);
                return Notify(new SwitchResult(SwitchStatus.Unchanged, slot, profile, target?.Profile ?? ModelProfile.Unbound,
                    SwitchReason.DeEscalation, sequence, Detail: "the session default is the configured one again")
                {
                    SessionDefaultChanged = true,
                    Pinned = IsPinned(slot),
                });
            }
            // Forgotten before the switch's notice, so a surface that reads the slot then sees no choice; kept when nothing was logged.
            lock (_state)
                _chosen.Remove(slot);
            try
            {
                return await SwitchCoreAsync(slot, target, SwitchReason.DeEscalation, pin: false, cancellationToken, clearedChoice: true)
                    .ConfigureAwait(false);
            }
            catch
            {
                lock (_state)
                    _chosen.TryAdd(slot, chosen);
                throw;
            }
        }, cancellationToken);

    /// <summary>Moves to the next escalation target the privacy gate allows.</summary>
    public Task<SwitchResult> EscalateAsync(string slot, CancellationToken cancellationToken = default) =>
        SerializedAsync(waitForTools: true,
            () => MoveAlongAsync(slot, Current(slot), configuration => configuration.Escalation, SwitchReason.Escalation, [], cancellationToken),
            cancellationToken);

    /// <summary>
    /// Moves past <paramref name="failed"/> along the fallback chain, skipping <paramref name="alsoFailed"/> (bindings
    /// that already failed this request). Returns <see cref="SwitchStatus.Unchanged"/> when the slot has already moved
    /// off the failed binding (a concurrent call fell back first): retry on the current one.
    /// </summary>
    /// <remarks>
    /// A fallback replaces one failed request and touches no issued tool call, so it does not wait for tracked calls:
    /// a tool whose own model call fails would otherwise wait for itself.
    /// </remarks>
    public Task<SwitchResult> FallBackAsync(string slot, ModelBinding failed, IReadOnlyCollection<ModelBinding>? alsoFailed = null,
        CancellationToken cancellationToken = default) =>
        SerializedAsync(waitForTools: false,
            () => MoveAlongAsync(slot, failed, configuration => configuration.Fallbacks, SwitchReason.Fallback, alsoFailed ?? [],
                cancellationToken),
            cancellationToken);

    /// <summary>
    /// The privacy egress gate refused a request to <paramref name="refused"/> (plan §10.4, "refuse and stay local"): move
    /// the slot to the first binding it lists that is not cloud, preferring <see cref="PrivacyClass.Local"/> (the default,
    /// then the fallbacks, then the escalation ladder), skipping <paramref name="alsoRefused"/>. Like a fallback it replaces
    /// one request, so it does not wait for tracked tool calls, and a pin stays where it was.
    /// </summary>
    public Task<SwitchResult> RetreatAsync(string slot, ModelBinding refused, IReadOnlyCollection<ModelBinding>? alsoRefused = null,
        CancellationToken cancellationToken = default) =>
        SerializedAsync(waitForTools: false, async () =>
        {
            ArgumentNullException.ThrowIfNull(refused);
            var current = Current(slot);
            if (!ReferenceEquals(current, refused))
                return new SwitchResult(SwitchStatus.Unchanged, slot, current.Profile, current.Profile, SwitchReason.Privacy,
                    Detail: "the slot already moved");
            var configuration = Configuration(slot);
            var skip = alsoRefused ?? [];
            var candidate = new[] { SessionDefault(slot), configuration.Default }.Concat(configuration.Fallbacks).Concat(configuration.Escalation)
                .Where(binding => !ReferenceEquals(binding, refused) && !skip.Contains(binding, ReferenceEqualityComparer.Instance)
                    && binding.Profile.Privacy != PrivacyClass.Cloud)
                .OrderBy(binding => binding.Profile.Privacy == PrivacyClass.Local ? 0 : 1)
                .FirstOrDefault();
            return candidate is null
                ? new SwitchResult(SwitchStatus.NoTarget, slot, refused.Profile, null, SwitchReason.Privacy,
                    Detail: $"no local model is configured for {slot}")
                : await SwitchCoreAsync(slot, candidate, SwitchReason.Privacy, pin: false, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// After a turn: every unpinned slot that is off its session default (<see cref="SessionDefault"/>) switches back (plan
    /// §6 de-escalation), to the person's choice for the session when they made one (A5), not to the registry's default.
    /// </summary>
    public Task<IReadOnlyList<SwitchResult>> CompleteTurnAsync(CancellationToken cancellationToken = default) =>
        SerializedAsync<IReadOnlyList<SwitchResult>>(waitForTools: true, async () =>
        {
            string[] slots;
            lock (_state)
                slots = _current.Keys.Where(slot => !_pinned.Contains(slot)).Order(StringComparer.Ordinal).ToArray();
            var results = new List<SwitchResult>();
            foreach (var slot in slots)
            {
                ModelBinding? target;
                lock (_state)
                    target = _chosen.GetValueOrDefault(slot);
                if (target is null && TryConfiguration(slot, out var configured))
                    target = configured.Default;
                // A slot only the person bound and then let go of while pinned has no default to go back to: it stays.
                if (target is not null && !ReferenceEquals(Current(slot), target))
                    results.Add(await SwitchCoreAsync(slot, target, SwitchReason.DeEscalation, pin: false, cancellationToken).ConfigureAwait(false));
            }
            return results;
        }, cancellationToken);

    // Runs one switch operation at a time. Tracked tool calls are awaited outside the lock, so a tool that needs the lock
    // (its own model call falls back) never waits on a switch that is itself waiting for that tool.
    private async Task<T> SerializedAsync<T>(bool waitForTools, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (waitForTools)
                await WaitForTrackedCallsAsync(cancellationToken).ConfigureAwait(false);
            await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (waitForTools && HasTrackedCalls())
                    continue;
                return await operation().ConfigureAwait(false);
            }
            catch (ToolsRunningException)
            {
                // A call was tracked after the check (seen on a retry after a concurrent append): wait for it and start again.
            }
            finally
            {
                _switching.Release();
            }
        }
    }

    private async Task<SwitchResult> MoveAlongAsync(string slot, ModelBinding from, Func<SlotConfiguration, ImmutableArray<ModelBinding>> list,
        SwitchReason reason, IReadOnlyCollection<ModelBinding> alsoFailed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        var current = Current(slot);
        if (!ReferenceEquals(current, from))
            return new(SwitchStatus.Unchanged, slot, current.Profile, current.Profile, reason, Detail: "the slot already moved");

        var configuration = Configuration(slot);
        var candidates = list(configuration);
        var index = candidates.IndexOf(from, ReferenceEqualityComparer.Instance);
        // Past a hop in the list, only move forward, so a chain never cycles back to an earlier hop. The default is the
        // starting point even when it is also listed (for example as the last resort), so from there start at the top. A
        // session default the person chose on the ladder (A5) is a hop like any other: escalation goes on past it.
        var start = index >= 0 && !ReferenceEquals(from, configuration.Default) ? index + 1 : 0;
        var refusals = new List<string>();
        foreach (var candidate in candidates[start..])
        {
            if (ReferenceEquals(candidate, from) || alsoFailed.Contains(candidate, ReferenceEqualityComparer.Instance))
                continue;
            var result = await SwitchCoreAsync(slot, candidate, reason, pin: false, cancellationToken).ConfigureAwait(false);
            if (result.Status != SwitchStatus.Refused)
                return result;
            refusals.Add($"{candidate.Profile.Id}: {result.Detail}");
        }
        return new(SwitchStatus.NoTarget, slot, from.Profile, null, reason,
            Detail: refusals.Count == 0 ? $"no {reason.ToWireName()} target after '{from.Profile.Id}'" : string.Join("; ", refusals));
    }

    // A null target unbinds the slot (only for a person's choice on a slot nothing configures: ClearSessionDefaultAsync).
    private async Task<SwitchResult> SwitchCoreAsync(string slot, ModelBinding? target, SwitchReason reason, bool pin,
        CancellationToken cancellationToken, bool clearedChoice = false)
    {
        // A5: a slot nothing binds (the decider on the degraded tier) can be given a model for the session; it switches from none.
        var from = TryCurrent(slot, out var bound) ? bound : null;
        var fromProfile = from?.Profile ?? ModelProfile.Unbound;
        var toProfile = target?.Profile ?? ModelProfile.Unbound;
        var wasPinned = IsPinned(slot);
        if (ReferenceEquals(from, target))
        {
            if (reason != SwitchReason.Session || target is null)
            {
                if (reason == SwitchReason.User)
                    SetPinned(slot, pin);
                return new(SwitchStatus.Unchanged, slot, fromProfile, toProfile, reason) { Pinned = IsPinned(slot) };
            }
            // A5: the person chose the model the slot already uses (an escalated one, say): it is the default from now on.
            bool changed;
            lock (_state)
                changed = !_chosen.TryGetValue(slot, out var previous) || !ReferenceEquals(previous, target);
            // A5 review: logged (from the model to itself), so the choice and its pin come back when the session is opened again.
            long? sequence = changed || wasPinned != pin
                ? AppendInPlace(slot, toProfile, SwitchReason.Session.ToWireName(), pinned: pin, reset: null, cancellationToken)
                : null;
            lock (_state)
                _chosen[slot] = target;
            SetPinned(slot, pin);
            var unchanged = new SwitchResult(SwitchStatus.Unchanged, slot, fromProfile, toProfile, reason, sequence)
            {
                SessionDefaultChanged = changed,
                Pinned = IsPinned(slot),
            };
            return sequence is not null ? Notify(unchanged) : unchanged;
        }

        StoredEvent? stored = null;
        LogState state = default;
        for (var attempt = 1; stored is null; attempt++)
        {
            // Step 1: issued tool calls finish and their results are logged; nothing in flight is cancelled. Tracked calls
            // were awaited before taking the lock; this checks the log. A fallback or a privacy retreat skips it (see FallBackAsync).
            state = ReadState(cancellationToken);
            if (reason is not (SwitchReason.Fallback or SwitchReason.Privacy))
                CheckIssuedWork(state);

            // Step 6, checked before the event so a refused move is never recorded as having happened.
            if (toProfile.Privacy == PrivacyClass.Cloud)
            {
                var verdict = await _options.Privacy.CheckAsync(
                    new(SessionId, slot, fromProfile, toProfile, reason, state.Head), cancellationToken).ConfigureAwait(false);
                if (!verdict.Allowed)
                    return new(SwitchStatus.Refused, slot, fromProfile, toProfile, reason, Detail: verdict.Reason);
            }

            // Step 3. Step 2 needs no action: pending approvals are events, and this append leaves them pending.
            // A5 review: the person's choice carries its pin, a turn switch that changes the pin says so, and a forgotten
            // choice says it was reset: a session opened again reads them.
            var change = new ModelSwitched(slot, fromProfile.ToSnapshot(), toProfile.ToSnapshot(), reason.ToWireName(),
                Pinned: reason == SwitchReason.Session || (reason == SwitchReason.User && wasPinned != pin) ? pin : null,
                Reset: clearedChoice ? true : null);
            try
            {
                stored = _log.Append(SessionId, state.Head, [EventData.Create(change)], cancellationToken)[0];
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
                // Another writer appended (for example a tool result); re-check against the new head.
            }
        }

        lock (_state)
        {
            if (target is null)
                _current.Remove(slot);
            else
                _current[slot] = target;
            // A5: the person's session choice is the slot's default from now on, set with the binding under the switch lock.
            if (reason == SwitchReason.Session && target is not null)
                _chosen[slot] = target;
        }
        // Only the user pins. Escalation and fallback keep a pin, so a fallen-back pinned slot stays put after the turn.
        if (reason is SwitchReason.User or SwitchReason.Session or SwitchReason.DeEscalation)
            SetPinned(slot, pin);

        // Step 4. Steps 5 (re-render for the new format) and 7 (continue) belong to the turn loop: the next call
        // through the slot uses the new binding.
        var budget = await _options.Budget.OnProfileChangedAsync(
            new(SessionId, slot, fromProfile, toProfile, stored.Sequence), cancellationToken).ConfigureAwait(false);
        return Notify(new SwitchResult(SwitchStatus.Switched, slot, fromProfile, toProfile, reason, stored.Sequence, budget,
            state.PendingApprovals, UnfinishedToolCalls: state.UnfinishedCalls)
        {
            SessionDefaultChanged = reason == SwitchReason.Session || clearedChoice,
            Pinned = IsPinned(slot),
        });
    }

    /// <summary>
    /// A5 review: a <c>ModelSwitched</c> from the slot's model to itself, for a change of the person's choice or pin that
    /// moves nothing (choosing the model in use, a pin, an unpin, a reset that leaves the slot where it is), so a session
    /// opened again restores what the person chose. Issued work is checked as for a switch (step 1); nothing else runs.
    /// </summary>
    private long AppendInPlace(string slot, ModelProfile profile, string reason, bool? pinned, bool? reset, CancellationToken cancellationToken)
    {
        var snapshot = profile.ToSnapshot();
        for (var attempt = 1; ; attempt++)
        {
            var state = ReadState(cancellationToken);
            CheckIssuedWork(state);
            try
            {
                return _log.Append(SessionId, state.Head, [EventData.Create(new ModelSwitched(slot, snapshot, snapshot, reason, pinned, reset))],
                    cancellationToken)[0].Sequence;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
                // Another writer appended (a tool result): re-check against the new head.
            }
        }
    }

    private SwitchResult Notify(SwitchResult result)
    {
        try
        {
            _options.Changed?.Invoke(result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A surface's handler must not fail the switch, which is already recorded.
        }
        return result;
    }

    private void SetPinned(string slot, bool pin)
    {
        lock (_state)
        {
            if (pin)
                _pinned.Add(slot);
            else
                _pinned.Remove(slot);
        }
    }

    private async Task WaitForTrackedCallsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] running;
            lock (_state)
                running = _inFlight.Values.Select(source => source.Task).ToArray();
            if (running.Length == 0)
                return;
            await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool HasTrackedCalls()
    {
        lock (_state)
            return _inFlight.Count > 0;
    }

    private void CheckIssuedWork(LogState state)
    {
        string[] orphans;
        lock (_state)
        {
            if (_inFlight.Count > 0)
                throw new ToolsRunningException();
            orphans = state.UnfinishedCalls.Where(call => !_parked.Contains(call)).ToArray();
        }
        if (orphans.Length > 0)
            throw new InvalidOperationException(
                $"Cannot switch session '{SessionId}': tool call(s) {string.Join(", ", orphans)} were issued, have no result " +
                "and are neither tracked nor parked. Track running calls with TrackToolCall and log their results first; " +
                "park calls that wait on an approval with ParkToolCall.");
    }

    // A small fold over the raw log. Corvid.Models cannot reference the kernel's projection (the kernel depends on Models).
    private LogState ReadState(CancellationToken cancellationToken)
    {
        var head = _log.GetHead(SessionId, cancellationToken);
        var calls = new List<string>();
        var approvals = new List<string>();
        for (long after = 0; after < head;)
        {
            var page = _log.Read(SessionId, after, head, ReadPage, cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var item in page)
            {
                switch (item.Payload)
                {
                    case ToolCallIssued call: calls.Add(call.CallId); break;
                    case ToolResult result: calls.Remove(result.CallId); break;
                    case ApprovalRequested request: approvals.Add(request.ApprovalId); break;
                    case ApprovalResolved resolved: approvals.Remove(resolved.ApprovalId); break;
                }
            }
            after = page[^1].Sequence;
        }
        return new(head, [.. calls], [.. approvals]);
    }

    private void Unpark(string callId)
    {
        lock (_state)
            _parked.Remove(callId);
    }

    private void Complete(string callId)
    {
        TaskCompletionSource? source;
        lock (_state)
        {
            if (!_inFlight.Remove(callId, out source))
                return;
        }
        source.TrySetResult();
    }

    private readonly record struct LogState(long Head, ImmutableArray<string> UnfinishedCalls, ImmutableArray<string> PendingApprovals);

    private sealed class ToolCallLease(SessionModelSlots owner, string callId) : IDisposable
    {
        public void Dispose() => owner.Complete(callId);
    }

    private sealed class ParkedCallLease(SessionModelSlots owner, string callId) : IDisposable
    {
        public void Dispose() => owner.Unpark(callId);
    }

    // Internal control flow only: SerializedAsync catches it, releases the lock and waits.
    private sealed class ToolsRunningException : Exception;
}
