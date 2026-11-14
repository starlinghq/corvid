using System.Collections.Immutable;
using Corvid.Abstractions.Events;

namespace Corvid.Kernel.Flocks;

/// <summary>A member of a flock: its session, the name the others address it by, and where in the flock's log it joined.</summary>
public sealed record FlockMember(string SessionId, string Name, long JoinedAt);

/// <summary>A message in a flock's log, at its sequence.</summary>
public sealed record FlockEntry(long Sequence, DateTimeOffset At, FlockMessage Message);

/// <summary>
/// C-042: a flock as its log stands at <see cref="Head"/>: its name, members, messages, what was delivered to whom, and the
/// loop guard. Folded from the log alone, so every process reads the same flock.
/// </summary>
public sealed class FlockState
{
    private readonly ImmutableHashSet<(long Sequence, string SessionId)> _delivered;

    private FlockState(string id, string name, long head, DateTimeOffset created, bool closed, bool paused, int peerTurns,
        ImmutableArray<FlockMember> members, ImmutableArray<FlockEntry> messages, ImmutableHashSet<(long, string)> delivered,
        ImmutableDictionary<string, string> departed)
    {
        Id = id;
        Name = name;
        Head = head;
        Created = created;
        Closed = closed;
        Paused = paused;
        PeerTurns = peerTurns;
        Members = members;
        Messages = messages;
        _delivered = delivered;
        _departed = departed;
    }

    /// <summary>The flock's log, <c>flock:…</c>.</summary>
    public string Id { get; }

    public string Name { get; }

    public long Head { get; }

    public DateTimeOffset Created { get; }

    public bool Closed { get; }

    /// <summary>The loop guard stopped delivering: members started too many turns on each other without the user.</summary>
    public bool Paused { get; }

    /// <summary>Turns members' messages started on each other since the user last posted to the flock or resumed it.</summary>
    public int PeerTurns { get; }

    /// <summary>Its members now, in the order they joined.</summary>
    public ImmutableArray<FlockMember> Members { get; }

    public ImmutableArray<FlockEntry> Messages { get; }

    /// <summary>The member a session is, or null.</summary>
    public FlockMember? Member(string sessionId) => Members.FirstOrDefault(member => member.SessionId == sessionId);

    /// <summary>A member by its name in the flock (case-insensitive) or its session id, or null.</summary>
    public FlockMember? Find(string nameOrId) => Members.FirstOrDefault(member => member.SessionId == nameOrId)
        ?? Members.FirstOrDefault(member => string.Equals(member.Name, nameOrId, StringComparison.OrdinalIgnoreCase));

    public bool WasDelivered(long sequence, string sessionId) => _delivered.Contains((sequence, sessionId));

    /// <summary>Who wrote a message, as the flock names them: "you" for the user, a member's name, or the session id of one gone.</summary>
    public string SenderName(FlockMessage message) => message.From == FlockSessions.User ? "you"
        : Member(message.From)?.Name ?? _departed.GetValueOrDefault(message.From) ?? message.From;

    // Members that left, by session id, so their messages keep their names.
    private readonly ImmutableDictionary<string, string> _departed;

    /// <summary>
    /// The messages still to hand to a member, oldest first: logged after it joined, for it (addressed to it or to everyone),
    /// not its own, and not yet delivered to it. Empty for a closed flock or a session that is not a member.
    /// </summary>
    public IReadOnlyList<FlockEntry> Pending(string sessionId)
    {
        if (Closed || Member(sessionId) is not { } member)
            return [];
        return [.. Messages.Where(entry => entry.Sequence > member.JoinedAt && entry.Message.From != sessionId
            && (entry.Message.To.IsDefaultOrEmpty || entry.Message.To.Contains(sessionId))
            && !WasDelivered(entry.Sequence, sessionId))];
    }

    /// <summary>Folds a flock's log. Throws <see cref="InvalidDataException"/> for a log that does not start with <see cref="FlockCreated"/>.</summary>
    public static FlockState Fold(string id, IEnumerable<StoredEvent> events)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string? name = null;
        long head = 0;
        var created = DateTimeOffset.MinValue;
        bool closed = false, paused = false;
        var peerTurns = 0;
        var members = new List<FlockMember>();
        var departed = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var messages = ImmutableArray.CreateBuilder<FlockEntry>();
        var bySequence = new Dictionary<long, FlockMessage>();
        var delivered = ImmutableHashSet.CreateBuilder<(long, string)>();
        foreach (var stored in events)
        {
            head = stored.Sequence;
            switch (stored.Payload)
            {
                case FlockCreated flock when name is null:
                    name = flock.Name;
                    created = stored.RecordedAt;
                    break;
                case FlockMemberJoined joined when !members.Exists(member => member.SessionId == joined.SessionId):
                    members.Add(new FlockMember(joined.SessionId, joined.Name, stored.Sequence));
                    departed.Remove(joined.SessionId);
                    break;
                case FlockMemberLeft left:
                    if (members.Find(member => member.SessionId == left.SessionId) is { } gone)
                    {
                        members.Remove(gone);
                        departed[gone.SessionId] = gone.Name;
                    }
                    break;
                case FlockMessage message:
                    messages.Add(new FlockEntry(stored.Sequence, stored.RecordedAt, message));
                    bySequence[stored.Sequence] = message;
                    // The user taking part resets the loop guard.
                    if (message.From == FlockSessions.User)
                        (paused, peerTurns) = (false, 0);
                    break;
                case FlockDelivered handed:
                    delivered.Add((handed.Sequence, handed.SessionId));
                    if (handed.Started && bySequence.TryGetValue(handed.Sequence, out var started) && started.From != FlockSessions.User)
                        peerTurns++;
                    break;
                case FlockPaused:
                    paused = true;
                    break;
                case FlockResumed:
                    (paused, peerTurns) = (false, 0);
                    break;
                case FlockClosed:
                    closed = true;
                    break;
            }
        }
        if (name is null)
            throw new InvalidDataException($"The log '{id}' is not a flock: it does not start with flock_created.");
        return new FlockState(id, name, head, created, closed, paused, peerTurns, [.. members], messages.ToImmutable(), delivered.ToImmutable(),
            departed.ToImmutable());
    }
}
