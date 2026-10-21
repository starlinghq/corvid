using Corvid.Abstractions.Events;

namespace Corvid.Gateway;

/// <summary>A queued delivery not yet resolved, with its position in the ledger.</summary>
public sealed record PendingDelivery(long Sequence, DeliveryQueued Delivery);

/// <summary>
/// The delivery ledger (plan §12): a table of the event log, kept as the <see cref="GatewaySessions.Ledger"/> session.
/// Producers append <see cref="DeliveryQueued"/> before anything is sent, from any process (a CLI that ran a scheduled job
/// queues its result here too); the gateway's worker tails the session, sends, and appends <see cref="DeliveryResolved"/>.
/// A delivery queued and never resolved is sent again, so delivery is at least once: a restart reads the ledger from its
/// low-water mark (every entry before it is resolved) and sends whatever is still open.
/// </summary>
public sealed class DeliveryLedger
{
    private const int MaxAppendAttempts = 32;
    private const int Page = 512;

    private readonly IEventLog _log;
    private readonly SortedDictionary<long, DeliveryQueued> _pending = [];
    // Only open deliveries are held, so a gateway that runs for months holds what is waiting, not everything it ever sent
    // (Hermes' gateway grew to about 8 GB, #18438).
    private readonly Dictionary<string, long> _byId = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _readThrough;

    public DeliveryLedger(IEventLog log, string sessionId = GatewaySessions.Ledger)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _log = log;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    /// <summary>Every entry at or before this position is resolved, so a restart can start reading after it.</summary>
    public long LowWater
    {
        get
        {
            // Never past what has been read: this process's own entries are tracked without a read, so another process's
            // entry may sit before them unread.
            lock (_gate)
                return _pending.Count > 0 ? Math.Min(_pending.Keys.First() - 1, _readThrough) : _readThrough;
        }
    }

    /// <summary>Open deliveries in ledger order.</summary>
    public IReadOnlyList<PendingDelivery> Pending
    {
        get
        {
            lock (_gate)
                return [.. _pending.Select(pair => new PendingDelivery(pair.Key, pair.Value))];
        }
    }

    /// <summary>
    /// Queues a delivery in <paramref name="log"/>'s ledger, retrying when another writer appended first. Usable from any
    /// process that shares the event log; the gateway picks it up on its next read.
    /// </summary>
    public static long Queue(IEventLog log, DeliveryQueued delivery, string sessionId = GatewaySessions.Ledger)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(delivery);
        return Append(log, sessionId, delivery);
    }

    /// <summary>Queues a delivery and starts tracking it at once (the gateway's own replies).</summary>
    public PendingDelivery Queue(DeliveryQueued delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var sequence = Append(_log, SessionId, delivery);
        lock (_gate)
            // A read that already passed this entry has tracked it, and perhaps seen it sent and resolved since.
            if (sequence > _readThrough && _byId.TryAdd(delivery.DeliveryId, sequence))
                _pending[sequence] = delivery;
        return new PendingDelivery(sequence, delivery);
    }

    /// <summary>Reads the ledger after <paramref name="after"/> (a stored low-water mark) and every entry since the last read.</summary>
    public void Catch(long after = -1)
    {
        lock (_gate)
        {
            if (after >= 0 && _readThrough == 0)
                _readThrough = after;
            while (true)
            {
                var page = _log.Read(SessionId, _readThrough, limit: Page);
                foreach (var stored in page)
                {
                    switch (stored.Payload)
                    {
                        // A delivery resolved in this process before this read reached it is tracked again here and
                        // dropped when the read reaches its resolution, which was appended first.
                        case DeliveryQueued queued:
                            if (_byId.TryAdd(queued.DeliveryId, stored.Sequence))
                                _pending[stored.Sequence] = queued;
                            break;
                        case DeliveryResolved resolved:
                            Forget(resolved.DeliveryId);
                            break;
                    }
                    _readThrough = stored.Sequence;
                }
                if (page.Count < Page)
                    return;
            }
        }
    }

    /// <summary>
    /// Records the outcome of an open delivery. A delivery is resolved once: one that is not open (resolved already, or not
    /// read yet) changes nothing.
    /// </summary>
    public void Resolve(string deliveryId, string status, string? platformMessageId = null, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);
        lock (_gate)
            if (!_byId.ContainsKey(deliveryId))
                return;
        Append(_log, SessionId, new DeliveryResolved(deliveryId, status, platformMessageId, detail));
        lock (_gate)
            Forget(deliveryId);
    }

    private void Forget(string deliveryId)
    {
        if (_byId.Remove(deliveryId, out var sequence))
            _pending.Remove(sequence);
    }

    private static long Append(IEventLog log, string sessionId, SessionEvent payload)
    {
        var events = new[] { EventData.Create(payload) };
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return log.Append(sessionId, log.GetHead(sessionId), events)[0].Sequence;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
                // Another process queued at the same moment; the head moved, so try at the new one.
            }
        }
    }
}
