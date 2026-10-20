using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;

namespace Corvid.Kernel.Events;

public sealed record TranscriptEntry(long FromSequence, long ThroughSequence, SessionEvent Content);
/// <summary>A persisted event the projection could not apply. The raw event stays in the log.</summary>
public sealed record ProjectionIssue(long Sequence, string Reason);
public sealed record DecisionState(Decision Decision, string? Outcome);

public sealed record SessionSnapshot(
    string SessionId,
    long Head,
    ImmutableArray<TranscriptEntry> Transcript,
    string? Goal,
    ImmutableArray<TodoItem> Todos,
    ImmutableDictionary<string, ApprovalRequested> PendingApprovals,
    ImmutableDictionary<string, JsonElement> ModelProfiles,
    ImmutableDictionary<string, DecisionState> Decisions,
    ImmutableArray<CheckpointTaken> Checkpoints,
    ImmutableArray<ProjectionIssue> Issues);

/// <summary>Pure replay of a complete session prefix. Compaction changes only the transcript view.</summary>
/// <remarks>
/// Caller errors (another session's events, gaps, a short prefix) throw. Inconsistent persisted events become
/// <see cref="SessionSnapshot.Issues"/> instead: an append-only log cannot repair them, and a session must stay readable.
/// </remarks>
public static class SessionProjection
{
    private const int PageSize = 4096;

    /// <summary>Projects a session at its head, or at an earlier position for resume, branch or undo.</summary>
    public static SessionSnapshot Load(IEventLog log, string sessionId, long? throughSequence = null,
        bool expandCompactions = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // Fix the end position first so events appended while paging cannot produce a torn snapshot.
        var through = throughSequence ?? log.GetHead(sessionId, cancellationToken);
        return Build(sessionId, ReadAll(log, sessionId, through, cancellationToken), through, expandCompactions);
    }

    internal static IEnumerable<StoredEvent> ReadAll(IEventLog log, string sessionId, long through,
        CancellationToken cancellationToken)
    {
        for (long after = 0; after < through;)
        {
            var page = log.Read(sessionId, after, through, PageSize, cancellationToken);
            if (page.Count == 0)
                yield break;
            foreach (var item in page)
                yield return item;
            after = page[^1].Sequence;
        }
    }

    public static SessionSnapshot Build(string sessionId, IEnumerable<StoredEvent> events,
        long? throughSequence = null, bool expandCompactions = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(events);
        if (throughSequence is < 0)
            throw new ArgumentOutOfRangeException(nameof(throughSequence));

        var history = new List<StoredEvent>();
        string? goal = null;
        var todos = ImmutableArray<TodoItem>.Empty;
        var approvals = ImmutableDictionary.CreateBuilder<string, ApprovalRequested>(StringComparer.Ordinal);
        var profiles = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        var decisions = ImmutableDictionary.CreateBuilder<string, DecisionState>(StringComparer.Ordinal);
        var checkpoints = ImmutableArray.CreateBuilder<CheckpointTaken>();
        var issues = ImmutableArray.CreateBuilder<ProjectionIssue>();
        // Every valid compaction, and the ones shown: not expanded and not inside another shown one. Validation of a new
        // range is against the shown ones, so nesting stays whole after an expansion reveals what an outer summary hid.
        var valid = new List<StoredEvent>();
        var expanded = new HashSet<long>();
        var compactions = new List<StoredEvent>();
        var calls = new Dictionary<string, long>(StringComparer.Ordinal);
        var results = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var item in events)
        {
            if (history.Count == throughSequence)
                break;
            if (item.SessionId != sessionId || item.Sequence != history.Count + 1L)
                throw new ArgumentException("Replay requires one session's complete, ordered prefix starting at sequence 1.", nameof(events));
            history.Add(item);
            switch (item.Payload)
            {
                case GoalSet value: goal = value.Goal; break;
                case TodosSet value: todos = value.Items; break;
                case ApprovalRequested value: approvals[value.ApprovalId] = value; break;
                case ApprovalResolved value:
                    if (!approvals.Remove(value.ApprovalId))
                        issues.Add(new(item.Sequence, $"Approval '{value.ApprovalId}' was resolved but is not pending."));
                    break;
                case ModelSwitched value: profiles[value.Slot] = value.NewProfile.Clone(); break;
                case Decision value:
                    // The first decision keeps the ID, so a later outcome cannot attach to the wrong answer.
                    if (!decisions.TryAdd(value.DecisionId, new(value, null)))
                        issues.Add(new(item.Sequence, $"Duplicate decision ID '{value.DecisionId}'."));
                    break;
                case DecisionOutcomeRecorded value:
                    if (decisions.TryGetValue(value.DecisionId, out var state))
                        decisions[value.DecisionId] = state with { Outcome = value.Outcome };
                    else
                        issues.Add(new(item.Sequence, $"Outcome for unknown decision '{value.DecisionId}'."));
                    break;
                case CheckpointTaken value: checkpoints.Add(value); break;
                case ToolCallIssued value:
                    if (!calls.TryAdd(value.CallId, item.Sequence))
                        issues.Add(new(item.Sequence, $"Duplicate tool call ID '{value.CallId}'."));
                    break;
                case ToolResult value:
                    if (!calls.ContainsKey(value.CallId) || !results.TryAdd(value.CallId, item.Sequence))
                        issues.Add(new(item.Sequence, $"Unmatched or duplicate tool result '{value.CallId}'."));
                    break;
                case Compacted value:
                    var reason = CompactionError(item, value, compactions, calls, results);
                    if (reason is not null)
                    {
                        // Bad summary metadata must never hide raw history or make a session unreadable.
                        issues.Add(new(item.Sequence, reason));
                        break;
                    }
                    valid.Add(item);
                    compactions = Shown(valid, expanded);
                    break;
                case CompactionExpanded value:
                    if (!valid.Any(old => old.Sequence == value.CompactedSequence))
                        issues.Add(new(item.Sequence, $"Expansion refers to no valid compaction at event {value.CompactedSequence}."));
                    else if (!expanded.Add(value.CompactedSequence))
                        issues.Add(new(item.Sequence, $"The compaction at event {value.CompactedSequence} is already expanded."));
                    else
                        compactions = Shown(valid, expanded);
                    break;
            }
        }
        if (throughSequence is { } through && history.Count != through)
            throw new ArgumentException("Replay input ends before the requested position.", nameof(events));

        var transcript = ImmutableArray.CreateBuilder<TranscriptEntry>();
        var ranges = expandCompactions ? [] : compactions.OrderBy(item => ((Compacted)item.Payload).FromSequence).ToArray();
        var rangeIndex = 0;
        foreach (var item in history)
        {
            while (rangeIndex < ranges.Length && item.Sequence > ((Compacted)ranges[rangeIndex].Payload).ThroughSequence)
                rangeIndex++;
            if (rangeIndex < ranges.Length && ranges[rangeIndex].Payload is Compacted range && Contains(range, item.Sequence))
            {
                if (item.Sequence == range.FromSequence)
                    transcript.Add(new(range.FromSequence, range.ThroughSequence, range));
                continue;
            }
            if (item.Payload is UserMessage or AssistantMessage or ToolCallIssued or ToolResult)
                transcript.Add(new(item.Sequence, item.Sequence, item.Payload));
        }

        return new(sessionId, history.Count, transcript.ToImmutable(), goal, todos,
            approvals.ToImmutable(), profiles.ToImmutable(), decisions.ToImmutable(), checkpoints.ToImmutable(), issues.ToImmutable());
    }

    private static string? CompactionError(StoredEvent item, Compacted range, List<StoredEvent> active,
        Dictionary<string, long> calls, Dictionary<string, long> results)
    {
        if (range.FromSequence < 1 || range.ThroughSequence < range.FromSequence || range.ThroughSequence >= item.Sequence)
            return "Range does not refer to prior history.";
        foreach (var old in active)
        {
            var prior = (Compacted)old.Payload;
            var overlaps = range.FromSequence <= prior.ThroughSequence && prior.FromSequence <= range.ThroughSequence;
            var contains = range.FromSequence <= prior.FromSequence && range.ThroughSequence >= prior.ThroughSequence;
            if ((overlaps || Contains(range, old.Sequence)) && !contains)
                return "Range partially replaces an existing summary; include its entire original range.";
        }
        foreach (var (callId, callSequence) in calls)
        {
            var hasResult = results.TryGetValue(callId, out var resultSequence);
            if (Contains(range, callSequence) != (hasResult && Contains(range, resultSequence)))
                return "Range splits a tool call from its result or contains an unfinished call.";
        }
        return null;
    }

    // Valid ranges are nested or disjoint, so the outermost unexpanded ones are those no other unexpanded range contains.
    private static List<StoredEvent> Shown(List<StoredEvent> valid, HashSet<long> expanded)
    {
        var candidates = valid.Where(item => !expanded.Contains(item.Sequence)).ToList();
        return [.. candidates.Where(item => !candidates.Any(other => !ReferenceEquals(other, item)
            && Encloses((Compacted)other.Payload, (Compacted)item.Payload, other.Sequence > item.Sequence)))];
    }

    // Equal ranges: the later summary is the one shown.
    private static bool Encloses(Compacted outer, Compacted inner, bool outerIsLater) =>
        outer.FromSequence <= inner.FromSequence && outer.ThroughSequence >= inner.ThroughSequence
        && (outerIsLater || outer.FromSequence != inner.FromSequence || outer.ThroughSequence != inner.ThroughSequence);

    private static bool Contains(Compacted range, long sequence) =>
        sequence >= range.FromSequence && sequence <= range.ThroughSequence;
}
