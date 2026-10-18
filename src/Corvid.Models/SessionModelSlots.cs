using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Models;

/// <summary>
/// One session's current slot bindings and the switch procedure of plan §4.2. Every change of binding is a
/// <c>ModelSwitched</c> event in the session log, appended before the new binding takes effect.
/// </summary>
/// <remarks>
/// Starting bindings come from the registry and are configuration, not events, so a session's projected
/// <c>ModelProfiles</c> list only the slots that have switched. Switches are serialized per session.
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

    public SessionModelSlots(ModelSlotRegistry registry, IEventLog log, string sessionId, ModelSwitchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _registry = registry;
        _log = log;
        _options = options ?? new ModelSwitchOptions();
        SessionId = sessionId;
        // Capture the defaults now, so rebinding the registry later cannot silently change this session's model.
        foreach (var slot in registry.BoundSlots)
            _current[slot] = registry.Get(slot).Default;
    }

    public string SessionId { get; }

    public ModelBinding Current(string slot)
    {
        lock (_state)
        {
            if (!_current.TryGetValue(slot, out var binding))
                _current[slot] = binding = _registry.Get(slot).Default;
            return binding;
        }
    }

    public bool IsPinned(string slot)
    {
        lock (_state)
            return _pinned.Contains(slot);
    }

    public void Unpin(string slot)
    {
        lock (_state)
            _pinned.Remove(slot);
    }

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
            if (!_inFlight.TryAdd(callId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)))
                throw new InvalidOperationException($"Tool call '{callId}' is already tracked.");
        }
        return new ToolCallLease(this, callId);
    }

    /// <summary>Switches a slot (a <c>/model</c> command, or an explicit escalation). A pinned slot is not de-escalated.</summary>
    public async Task<SwitchResult> SwitchAsync(string slot, ModelBinding target, SwitchReason reason = SwitchReason.User,
        bool pin = false, CancellationToken cancellationToken = default)
    {
        ModelSlots.Check(slot, target);
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SwitchCoreAsync(slot, target, reason, pin, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>Moves to the next escalation target the privacy gate allows.</summary>
    public Task<SwitchResult> EscalateAsync(string slot, CancellationToken cancellationToken = default) =>
        MoveAlongAsync(slot, Current(slot), configuration => configuration.Escalation, SwitchReason.Escalation, cancellationToken);

    /// <summary>
    /// Moves past <paramref name="failed"/> along the fallback chain. Returns <see cref="SwitchStatus.Unchanged"/> when
    /// the slot has already moved off the failed binding (a concurrent call fell back first): retry on the current one.
    /// </summary>
    public Task<SwitchResult> FallBackAsync(string slot, ModelBinding failed, CancellationToken cancellationToken = default) =>
        MoveAlongAsync(slot, failed, configuration => configuration.Fallbacks, SwitchReason.Fallback, cancellationToken);

    /// <summary>After a turn: every unpinned slot that is off its default switches back (plan §6 de-escalation).</summary>
    public async Task<IReadOnlyList<SwitchResult>> CompleteTurnAsync(CancellationToken cancellationToken = default)
    {
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] slots;
            lock (_state)
                slots = _current.Keys.Where(slot => !_pinned.Contains(slot)).Order(StringComparer.Ordinal).ToArray();
            var results = new List<SwitchResult>();
            foreach (var slot in slots)
            {
                var target = _registry.Get(slot).Default;
                if (!ReferenceEquals(Current(slot), target))
                    results.Add(await SwitchCoreAsync(slot, target, SwitchReason.DeEscalation, pin: false, cancellationToken).ConfigureAwait(false));
            }
            return results;
        }
        finally
        {
            _switching.Release();
        }
    }

    private async Task<SwitchResult> MoveAlongAsync(string slot, ModelBinding from, Func<SlotConfiguration, ImmutableArray<ModelBinding>> list,
        SwitchReason reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Current(slot);
            if (!ReferenceEquals(current, from))
                return new(SwitchStatus.Unchanged, slot, current.Profile, current.Profile, reason, Detail: "the slot already moved");

            var candidates = list(_registry.Get(slot));
            var index = candidates.IndexOf(from, ReferenceEqualityComparer.Instance);
            var refusals = new List<string>();
            // Inside the list, only move forward, so a chain can never cycle back to a binding that failed.
            foreach (var candidate in index >= 0 ? candidates[(index + 1)..] : candidates)
            {
                if (ReferenceEquals(candidate, from))
                    continue;
                var result = await SwitchCoreAsync(slot, candidate, reason, pin: false, cancellationToken).ConfigureAwait(false);
                if (result.Status != SwitchStatus.Refused)
                    return result;
                refusals.Add($"{candidate.Profile.Id}: {result.Detail}");
            }
            return new(SwitchStatus.NoTarget, slot, from.Profile, null, reason,
                Detail: refusals.Count == 0 ? $"no {reason.ToWireName()} target after '{from.Profile.Id}'" : string.Join("; ", refusals));
        }
        finally
        {
            _switching.Release();
        }
    }

    private async Task<SwitchResult> SwitchCoreAsync(string slot, ModelBinding target, SwitchReason reason, bool pin,
        CancellationToken cancellationToken)
    {
        var from = Current(slot);
        if (ReferenceEquals(from, target))
        {
            if (reason == SwitchReason.User)
                SetPinned(slot, pin);
            return new(SwitchStatus.Unchanged, slot, from.Profile, target.Profile, reason);
        }

        StoredEvent? stored = null;
        LogState state = default;
        for (var attempt = 1; stored is null; attempt++)
        {
            // Step 1: issued tool calls finish and their results are logged; nothing in flight is cancelled.
            state = await WaitForIssuedWorkAsync(cancellationToken).ConfigureAwait(false);

            // Step 6, checked before the event so a refused move is never recorded as having happened.
            if (target.Profile.Privacy == PrivacyClass.Cloud)
            {
                var verdict = await _options.Privacy.CheckAsync(
                    new(SessionId, slot, from.Profile, target.Profile, reason, state.Head), cancellationToken).ConfigureAwait(false);
                if (!verdict.Allowed)
                    return new(SwitchStatus.Refused, slot, from.Profile, target.Profile, reason, Detail: verdict.Reason);
            }

            // Step 3. Step 2 needs no action: pending approvals are events, and this append leaves them pending.
            var change = new ModelSwitched(slot, from.Profile.ToSnapshot(), target.Profile.ToSnapshot(), reason.ToWireName());
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
            _current[slot] = target;
        // Only the user pins. Escalation and fallback keep a pin, so a fallen-back pinned slot stays put after the turn.
        if (reason is SwitchReason.User or SwitchReason.DeEscalation)
            SetPinned(slot, pin);

        // Step 4. Steps 5 (re-render for the new format) and 7 (continue) belong to the turn loop: the next call
        // through the slot uses the new binding.
        var budget = await _options.Budget.OnProfileChangedAsync(
            new(SessionId, slot, from.Profile, target.Profile, stored.Sequence), cancellationToken).ConfigureAwait(false);
        return new(SwitchStatus.Switched, slot, from.Profile, target.Profile, reason, stored.Sequence, budget, state.PendingApprovals);
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

    private async Task<LogState> WaitForIssuedWorkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task[] running;
            lock (_state)
                running = _inFlight.Values.Select(source => source.Task).ToArray();
            if (running.Length > 0)
            {
                await Task.WhenAll(running).WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var state = ReadState(cancellationToken);
            if (state.UnfinishedCalls.IsEmpty)
                return state;
            lock (_state)
            {
                if (_inFlight.Count > 0)
                    continue;
            }
            throw new InvalidOperationException(
                $"Cannot switch session '{SessionId}': tool call(s) {string.Join(", ", state.UnfinishedCalls)} were issued, " +
                "have no result and are not tracked. Track running calls with TrackToolCall and log their results first.");
        }
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
}
