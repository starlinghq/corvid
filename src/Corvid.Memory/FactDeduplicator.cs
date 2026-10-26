using System.Globalization;
using System.Text;
using Corvid.Abstractions.Events;

namespace Corvid.Memory;

/// <summary>
/// Plan §9.5's fact dedupe, the conservative half: current facts that say the same thing (the same words, ignoring case,
/// spacing and a closing full stop; the same relation, if any; the same local-only mark) are found, the earliest kept and
/// ending each later one staged for review as an <c>invalidate</c> (<see cref="MemoryWriter.StageDuplicate"/>). Nothing is
/// ended unapproved, and an ending a person rejected is not proposed again. Merging facts that say the same thing in other
/// words needs judgement (a decider or the generator) and is not done here.
/// </summary>
public sealed class FactDeduplicator
{
    private readonly FactGraph _facts;
    private readonly MemoryWriter _writer;
    private readonly IEventLog _log;
    private readonly TimeProvider _time;

    public FactDeduplicator(FactGraph facts, MemoryWriter writer, IEventLog log, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);
        _facts = facts;
        _writer = writer;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Stages the ending of each duplicate found now; appends <see cref="MemoryJobRan"/> for each to <paramref name="sessionId"/>.</summary>
    public IReadOnlyList<PendingWrite> StageDuplicates(string sessionId = MemoryJobs.Session, string trigger = "idle")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var staged = new List<PendingWrite>();
        var current = _facts.AsOf(_time.GetUtcNow()).Where(fact => fact.IsCurrent);
        foreach (var group in current.GroupBy(fact => (Key(fact.Text), fact.SubjectId, fact.Predicate, fact.ObjectId, fact.LocalOnly))
                     .Where(group => group.Count() > 1))
        {
            var ordered = group.OrderBy(fact => fact.ValidFrom).ThenBy(fact => fact.RecordedAt).ThenBy(fact => fact.FactId, StringComparer.Ordinal).ToList();
            var kept = ordered[0];
            foreach (var duplicate in ordered.Skip(1))
            {
                if (_writer.StageDuplicate(sessionId, duplicate, kept) is not { Pending: { } pending })
                    continue;
                staged.Add(pending);
                EventAppend.Append(_log, sessionId, new MemoryJobRan(MemoryJobs.DedupeFacts, duplicate.FactId, "staged",
                    string.Create(CultureInfo.InvariantCulture, $"says what {kept.FactId} says"), trigger, pending.PendingId));
            }
        }
        return staged;
    }

    /// <summary>The words that make two statements the same: case, spacing and a closing full stop ignored, NFKC.</summary>
    internal static string Key(string text)
    {
        var words = string.Join(' ', text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return words.TrimEnd('.', ' ').ToLowerInvariant();
    }
}
