using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Corvid.Abstractions.Events;

// C-042: a flock is a few top-level sessions working on one task together, smaller and shorter-lived than a project. Each
// flock has its own log (session id "flock:<8 hex>", FlockSessions), which is both its record and its channel: members and
// the user append messages to it, and the process driving each member delivers the messages addressed to it from there, so
// a terminal's session and the app's can talk across the G-12 driver lock and nothing waits on a live delivery. Never a
// conversation: lists of sessions leave flock logs out.

// The first event of a flock's log. Name is what the user called it ("auth-refactor"): one line, unique among open flocks.
public sealed record FlockCreated(string Name) : SessionEvent;

// A session joined. Name is how the others address it ("api"), unique among the flock's members; the session's title or a
// short id when the user gave none. Only messages logged after this event are delivered to it.
public sealed record FlockMemberJoined(string SessionId, string Name) : SessionEvent;

public sealed record FlockMemberLeft(string SessionId) : SessionEvent;

// A message in the flock. From is a member's session id, or FlockSessions.User for the user's own (from the flock's view,
// /flock say, corvid flock say). To names the members it is for, by session id; empty for every member but its sender.
// Tainted: the sending session held outside content when it sent it, so it reaches the others as outside content too
// (plan §10.3); a member's untainted message neither taints nor clears its receiver, and the user's clears it as the
// user's own message does.
public sealed record FlockMessage(string From, string Text, ImmutableArray<string> To, bool Tainted = false) : SessionEvent;

// The message at Sequence in this log was handed to the member SessionId. Started: it started a turn (the member was idle,
// or it was queued behind the running one); false for a steer into the running turn. The loop guard counts the started ones.
public sealed record FlockDelivered(long Sequence, string SessionId, bool Started) : SessionEvent;

// The loop guard (C-042): members started Turns turns on each other's messages without the user taking part, so delivering
// stops until the user posts to the flock or resumes it. Messages sent meanwhile wait in the log.
public sealed record FlockPaused(int Turns) : SessionEvent;

public sealed record FlockResumed : SessionEvent;

// The user closed the flock: nothing more is delivered or accepted, and it leaves the lists of open flocks. The log stays.
public sealed record FlockClosed : SessionEvent;

public static class FlockSessions
{
    /// <summary>What every flock log's session id starts with.</summary>
    public const string Prefix = "flock:";

    /// <summary><see cref="FlockMessage.From"/> for the user's own message.</summary>
    public const string User = "user";

    public static bool IsFlock(string sessionId) => sessionId.StartsWith(Prefix, StringComparison.Ordinal);
}
