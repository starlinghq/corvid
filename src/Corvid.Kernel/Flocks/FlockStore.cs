using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Kernel.Flocks;

/// <summary>Why a flock operation was refused, in a sentence for the person (or the model) who asked.</summary>
public sealed class FlockException(string message) : InvalidOperationException(message);

/// <summary>
/// C-042: flocks in the event log, one log each (<c>flock:&lt;8 hex&gt;</c>, from the name: <see cref="IdFor"/>). Every write re-reads the flock and appends
/// against its head, retrying on a concurrent append, so the chat in one terminal, the app and <c>corvid flock</c> in another
/// write the same flock safely. Reads fold the log, cached by head. Thread-safe.
/// </summary>
public sealed class FlockStore(SqliteEventLog log)
{
    /// <summary>The longest flock or member name.</summary>
    public const int MaxName = 40;

    /// <summary>The longest message, in characters; a longer one is refused, not cut.</summary>
    public const int MaxText = 8_000;

    /// <summary>Turns members may start on each other's messages before the flock pauses for the user.</summary>
    public const int DefaultMaxPeerTurns = 8;

    private readonly ConcurrentDictionary<string, FlockState> _cache = new(StringComparer.Ordinal);

    /// <summary>The flock log's events, for the flock view's transcript.</summary>
    public SqliteEventLog Log { get; } = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>Flocks, the most recently created first; closed ones only when asked for.</summary>
    public IReadOnlyList<FlockState> List(bool includeClosed = false) =>
        [.. Log.ListHeads(FlockSessions.Prefix).Select(pair => Read(pair.Key, pair.Value)).OfType<FlockState>()
            .Where(flock => includeClosed || !flock.Closed).OrderByDescending(flock => flock.Created)];

    /// <summary>A flock as it stands now, or null when the log is not one.</summary>
    public FlockState? Get(string flockId) => FlockSessions.IsFlock(flockId) ? Read(flockId, Log.GetHead(flockId)) : null;

    /// <summary>
    /// An open flock by its name (case-insensitive), its id, or its id without <c>flock:</c>; then a closed one by id.
    /// </summary>
    public FlockState? Find(string nameOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);
        var wanted = nameOrId.Trim();
        var open = List();
        return open.FirstOrDefault(flock => string.Equals(flock.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? Get(wanted) ?? Get(FlockSessions.Prefix + wanted);
    }

    /// <summary>The open flock a session is a member of, or null. A session is in one open flock at most.</summary>
    public FlockState? FlockOf(string sessionId) => List().FirstOrDefault(flock => flock.Member(sessionId) is not null);

    /// <summary>Starts a flock. Refused when an open flock has the name.</summary>
    public FlockState Create(string name) => Open(name, join: false);

    /// <summary>
    /// The open flock by that name, started when there is none: what <c>corvid chat --flock</c> joins. Two processes asking
    /// for one new name at once get one flock.
    /// </summary>
    public FlockState FindOrCreate(string name) =>
        Find(name) is { Closed: false } open ? open : Open(name, join: true);

    // A flock's id follows from its name and a generation (the first name's log, else the next once it is closed), so two
    // processes starting one name append to one log: the second's append conflicts and it finds the first's flock.
    private FlockState Open(string name, bool join)
    {
        var clean = CleanName(name, "flock");
        if (List().FirstOrDefault(flock => string.Equals(flock.Name, clean, StringComparison.OrdinalIgnoreCase)) is { } named)
            return join ? named : throw new FlockException($"A flock named {clean} is already open.");
        for (var generation = 1; ; generation++)
        {
            var id = IdFor(clean, generation);
            if (Get(id) is { } existing)
            {
                if (existing.Closed)
                    continue;
                return join ? existing : throw new FlockException($"A flock named {existing.Name} is already open.");
            }
            try
            {
                Log.Append(id, 0, [EventData.Create(new FlockCreated(clean))]);
                return Get(id)!;
            }
            catch (EventLogConflictException)
            {
                // Another process started it a moment ago: read it on the next pass of the loop.
                generation--;
            }
        }
    }

    /// <summary>A flock's log for a name (case-insensitive) and generation: <c>flock:</c> and 8 hex digits of a hash.</summary>
    internal static string IdFor(string name, int generation) => FlockSessions.Prefix + Convert.ToHexStringLower(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant() + "\n" + generation)))[..8];

    /// <summary>
    /// Adds a session, named <paramref name="name"/> (made unique with <c>-2</c>, <c>-3</c>…). Refused for a closed flock,
    /// a session in another open flock, and anything but a top-level session (a child's parent speaks for it, and a flock
    /// log is not a session); joining the flock it is in already returns it unchanged.
    /// </summary>
    public FlockState Join(string flockId, string sessionId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // FlockSessions.User is the user's own name in a flock's messages: a session by that id would speak as the user.
        if (sessionId == FlockSessions.User)
            throw new FlockException($"'{FlockSessions.User}' is reserved for your own messages and cannot be a member.");
        if (FlockSessions.IsFlock(sessionId) || Log.Read(sessionId, 0, 1, 1) is [{ Payload: ChildOf }])
            throw new FlockException("Only a top-level session can join a flock; a child session's parent speaks for it.");
        var clean = CleanName(name, "member");
        if (FlockOf(sessionId) is { } other && other.Id != flockId)
            throw new FlockException($"This session is in the flock {other.Name}; leave it first.");
        return Write(flockId, flock =>
        {
            if (flock.Member(sessionId) is not null)
                return [];
            var unique = clean;
            for (var n = 2; flock.Members.Any(member => string.Equals(member.Name, unique, StringComparison.OrdinalIgnoreCase)); n++)
                unique = $"{clean}-{n}";
            return [new FlockMemberJoined(sessionId, unique)];
        });
    }

    public FlockState Leave(string flockId, string sessionId) => Write(flockId, flock =>
        flock.Member(sessionId) is null ? throw new FlockException($"This session is not in the flock {flock.Name}.") : [new FlockMemberLeft(sessionId)]);

    /// <summary>
    /// Logs a message from a member (by session id) or the user (<see cref="FlockSessions.User"/>) to the members named in
    /// <paramref name="to"/> (session ids; empty for every other member) and returns its sequence. Refused for a closed flock,
    /// a sender that is not a member, a recipient that is not one, an empty message or one over <see cref="MaxText"/>.
    /// </summary>
    public long Say(string flockId, string from, string text, IReadOnlyCollection<string> to, bool tainted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentNullException.ThrowIfNull(to);
        if (string.IsNullOrWhiteSpace(text))
            throw new FlockException("The message is empty.");
        if (text.Length > MaxText)
            throw new FlockException($"The message is {text.Length:N0} characters; the most is {MaxText:N0}. Send a shorter one, or point at a file.");
        var appended = Append(flockId, flock =>
        {
            if (from != FlockSessions.User && flock.Member(from) is null)
                throw new FlockException($"This session is not in the flock {flock.Name}.");
            if (to.FirstOrDefault(id => flock.Member(id) is null) is { } stranger)
                throw new FlockException($"{stranger} is not a member of the flock {flock.Name}.");
            if (to.Count == 0 && !flock.Members.Any(member => member.SessionId != from))
                throw new FlockException($"Nobody else is in the flock {flock.Name} yet.");
            return [new FlockMessage(from, text.Trim(), [.. to.Distinct(StringComparer.Ordinal)], tainted)];
        });
        return appended[^1].Sequence;
    }

    /// <summary>Records that the message at <paramref name="sequence"/> was handed to a member (idempotent).</summary>
    public FlockState Delivered(string flockId, long sequence, string sessionId, bool started) => Write(flockId, flock =>
        flock.WasDelivered(sequence, sessionId) ? [] : [new FlockDelivered(sequence, sessionId, started)]);

    /// <summary>
    /// Pauses delivery for the loop guard, once, when the flock as it stands now has reached <paramref name="maxPeerTurns"/>:
    /// a flock paused already, or one the user wrote to since (which reset the count), is returned unchanged.
    /// </summary>
    public FlockState Pause(string flockId, int maxPeerTurns) =>
        Write(flockId, flock => flock.Paused || flock.PeerTurns < maxPeerTurns ? [] : [new FlockPaused(flock.PeerTurns)]);

    public FlockState Resume(string flockId) => Write(flockId, flock => [new FlockResumed()]);

    public FlockState Close(string flockId) => Write(flockId, flock => flock.Closed ? [] : [new FlockClosed()]);

    // Appends what the change makes of the flock as it stands, against its head; a concurrent append reads it again.
    private FlockState Write(string flockId, Func<FlockState, IReadOnlyList<SessionEvent>> change)
    {
        Append(flockId, change);
        return Get(flockId)!;
    }

    // As Write, returning what this call appended (empty when the change made nothing).
    private IReadOnlyList<StoredEvent> Append(string flockId, Func<FlockState, IReadOnlyList<SessionEvent>> change)
    {
        for (var attempt = 0; ; attempt++)
        {
            var flock = Get(flockId) ?? throw new FlockException($"There is no flock {flockId}.");
            if (flock.Closed)
                throw new FlockException($"The flock {flock.Name} is closed.");
            var events = change(flock);
            if (events.Count == 0)
                return [];
            try
            {
                return Log.Append(flockId, flock.Head, [.. events.Select(EventData.Create)]);
            }
            catch (EventLogConflictException) when (attempt < 8)
            {
            }
        }
    }

    private FlockState? Read(string flockId, long head)
    {
        if (head == 0)
            return null;
        if (_cache.TryGetValue(flockId, out var cached) && cached.Head == head)
            return cached;
        var events = new List<StoredEvent>();
        for (long after = 0; after < head;)
        {
            var page = Log.Read(flockId, after, head);
            if (page.Count == 0)
                break;
            events.AddRange(page);
            after = page[^1].Sequence;
        }
        if (events.Count == 0 || events[0].Payload is not FlockCreated)
            return null;
        var state = FlockState.Fold(flockId, events);
        _cache[flockId] = state;
        return state;
    }

    /// <summary>
    /// A flock or member name as it is kept: one line, no spaces (they become dashes, so <c>@name</c> addresses it), visible
    /// characters only, at most <see cref="MaxName"/>. Refused when nothing is left.
    /// </summary>
    public static string CleanName(string? name, string what)
    {
        var kept = new string([.. (name ?? "").Trim().Select(c => char.IsWhiteSpace(c) ? '-' : c)
            .Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format && c != '@')]);
        while (kept.Contains("--", StringComparison.Ordinal))
            kept = kept.Replace("--", "-", StringComparison.Ordinal);
        kept = kept.Trim('-');
        if (kept.Length == 0)
            throw new FlockException($"A {what} needs a name.");
        return kept.Length <= MaxName ? kept : kept[..MaxName].TrimEnd('-');
    }
}
