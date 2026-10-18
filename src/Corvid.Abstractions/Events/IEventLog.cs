namespace Corvid.Abstractions.Events;

public sealed record EventData(Guid EventId, SessionEvent Payload)
{
    public static EventData Create(SessionEvent payload) => new(Guid.NewGuid(), payload);
}

public sealed record StoredEvent(string SessionId, long Sequence, Guid EventId, DateTimeOffset RecordedAt, SessionEvent Payload);

/// <summary>Durable, ordered session history. Positions are one-based; an empty session has head zero.</summary>
/// <remarks>SQLite operations are synchronous. Call from a storage worker if the caller must not block.</remarks>
public interface IEventLog
{
    long GetHead(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Appends the entire non-empty batch or nothing. Expected head prevents lost concurrent writes.</summary>
    IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default);

    /// <summary>Reads (afterSequence, throughSequence], ordered and limited. Capture a head for stable paging.</summary>
    IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue,
        int limit = 256, CancellationToken cancellationToken = default);
}

public sealed class EventLogConflictException(string sessionId, long expectedHead, long actualHead)
    : InvalidOperationException($"Session '{sessionId}' expected head {expectedHead}, but its head is {actualHead}.")
{
    public string SessionId { get; } = sessionId;
    public long ExpectedHead { get; } = expectedHead;
    public long ActualHead { get; } = actualHead;
}
