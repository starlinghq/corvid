using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Kernel.Sessions;

/// <summary>Why a fork was refused; nothing was written.</summary>
public sealed class SessionForkException(string message) : InvalidOperationException(message);

/// <summary>
/// Forks a session at a log position (plan §3.2: branching and forking reduce to choosing a position): the source's events
/// up to and including that position are copied, in order and at the same sequences, into a new, empty session. The source
/// is never changed. Sequences inside payloads (a compaction's range, a rewind's position) keep their meaning because the
/// copy keeps every sequence; event ids are new, since an id names one row of the log.
/// </summary>
/// <remarks>
/// The fork shares the workspace with its source: the write ledger (C-014) is copied with the events, so an undo in the
/// fork reverts the source's file changes before the position as an undo in the source would.
/// </remarks>
public static class SessionFork
{
    private const int Page = 256;

    /// <summary>Copies <paramref name="source"/> through <paramref name="throughSequence"/> (its head when null) into <paramref name="target"/>.</summary>
    /// <returns>The position copied, which is the fork's head.</returns>
    /// <exception cref="SessionForkException">The position is outside the log, leaves a call or an approval open, or the target is not empty.</exception>
    public static long Fork(IEventLog log, string source, string target, long? throughSequence = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (string.Equals(source, target, StringComparison.Ordinal))
            throw new SessionForkException("A session cannot be forked into itself.");
        var head = log.GetHead(source, cancellationToken);
        if (head == 0)
            throw new SessionForkException($"Session '{source}' has no events to fork.");
        var through = throughSequence ?? head;
        if (through < 1 || through > head)
            throw new SessionForkException($"Position {through} is outside session '{source}' (1 to {head}).");
        if (log.GetHead(target, cancellationToken) != 0)
            throw new SessionForkException($"Session '{target}' already exists.");

        // The position must be a resting point: every call issued by then has its result by then, and no approval is waiting,
        // or the fork would start with a call it can never finish or a prompt nobody can answer.
        var snapshot = SessionProjection.Load(log, source, through, cancellationToken: cancellationToken);
        if (!snapshot.PendingApprovals.IsEmpty)
            throw new SessionForkException($"Position {through} is inside an approval prompt; fork after it is answered.");
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var written = 0L;
        for (var after = 0L; after < through;)
        {
            var page = log.Read(source, after, through, Page, cancellationToken);
            if (page.Count == 0)
                break;
            foreach (var item in page)
            {
                if (item.Payload is ToolCallIssued call)
                    calls.Add(call.CallId);
                else if (item.Payload is ToolResult result)
                    calls.Remove(result.CallId);
            }
            after = page[^1].Sequence;
        }
        if (calls.Count > 0)
            throw new SessionForkException($"Position {through} leaves tool call '{calls.Order(StringComparer.Ordinal).First()}' without its result.");

        for (var after = 0L; after < through;)
        {
            var page = log.Read(source, after, through, Page, cancellationToken);
            if (page.Count == 0)
                break;
            // The target was empty and nothing else writes it until the fork returns, so a conflict means another fork raced.
            try
            {
                log.Append(target, written, [.. page.Select(item => EventData.Create(item.Payload))], cancellationToken);
            }
            catch (EventLogConflictException)
            {
                throw new SessionForkException($"Session '{target}' was written by someone else while it was being forked.");
            }
            written += page.Count;
            after = page[^1].Sequence;
        }
        return written;
    }
}
