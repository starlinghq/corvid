using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;

namespace Corvid.Evals.Calibration;

/// <summary>Which tier answered, read from the <c>decider</c> field of a <c>Decision</c> event.</summary>
public enum DecisionTier
{
    /// <summary>A calibrated decider (Kev, C-004b). Only these answers fit floors.</summary>
    Calibrated,
    /// <summary>The degraded tier (plan §5.6), including a fallback after the calibrated decider declined. Uncalibrated.</summary>
    Degraded,
    /// <summary>No decider recorded: a decision written before C-004a. Treated as uncalibrated.</summary>
    Unknown,
}

/// <summary>How a recorded outcome judges the decider's answer.</summary>
public enum OutcomeLabel
{
    /// <summary>No outcome was recorded.</summary>
    None,
    Positive,
    Negative,
    /// <summary>An outcome the classifier does not know. Reported, never used for fitting.</summary>
    Unlabelled,
}

/// <summary>
/// Maps outcome strings to labels (plan §5.5: corrections, denials, retries and tool failures are negative). Outcomes are
/// free text in the log; anything not listed is <see cref="OutcomeLabel.Unlabelled"/> rather than guessed.
/// </summary>
public sealed class OutcomeClassifier
{
    public OutcomeClassifier(IEnumerable<string> positive, IEnumerable<string> negative)
    {
        ArgumentNullException.ThrowIfNull(positive);
        ArgumentNullException.ThrowIfNull(negative);
        Positive = positive.ToImmutableSortedSet(StringComparer.Ordinal);
        Negative = negative.ToImmutableSortedSet(StringComparer.Ordinal);
        if (Positive.Overlaps(Negative))
            throw new ArgumentException("An outcome cannot be both positive and negative.");
    }

    /// <summary>The outcomes Corvid writes today (C-004a tests, C-008's review path, the golden set) and their user_ forms.</summary>
    public static OutcomeClassifier Default { get; } = new(
        ["accepted", "approved", "succeeded", "task_succeeded", "tool_succeeded", "user_accepted", "user_approved"],
        ["corrected", "denied", "failed", "rejected", "retried", "task_failed", "tool_failed", "user_corrected", "user_denied",
         "user_rejected", "user_retried"]);

    public ImmutableSortedSet<string> Positive { get; }

    public ImmutableSortedSet<string> Negative { get; }

    public OutcomeLabel Classify(string? outcome) =>
        outcome is null ? OutcomeLabel.None
        : Positive.Contains(outcome) ? OutcomeLabel.Positive
        : Negative.Contains(outcome) ? OutcomeLabel.Negative
        : OutcomeLabel.Unlabelled;
}

/// <summary>One <c>Decision</c> event joined with its outcomes, and what the catalogue says about it.</summary>
/// <param name="Source">The event log file the decision came from.</param>
/// <param name="Key">The request key as stored (the event's <c>question_id</c>), for example <c>clear_results/call-7</c>.</param>
/// <param name="QuestionId">The catalogue id: the key up to the first <c>/</c>.</param>
/// <param name="Instance">The key's instance suffix after the <c>/</c>, or null.</param>
/// <param name="Question">The catalogue row, or null for a key the catalogue does not know.</param>
/// <param name="Answer">The answer rebuilt from the stored probabilities, or null when none was given (an empty map).</param>
/// <param name="Measure">The value the floor is compared with (<see cref="DecisionPolicy.MeasureOf"/>), or null without an answer.</param>
/// <param name="Acted">Whether the answer cleared its threshold and was acted on.</param>
/// <param name="Outcomes">Every outcome recorded for the decision, in log order. The last one labels it.</param>
public sealed record DecisionRecord(
    string Source,
    string SessionId,
    long Sequence,
    DateTimeOffset RecordedAt,
    Decision Event,
    string Key,
    string QuestionId,
    string? Instance,
    CatalogueQuestion? Question,
    DecisionTier Tier,
    DecisionAnswer? Answer,
    double? Measure,
    bool Acted,
    ImmutableArray<string> Outcomes,
    OutcomeLabel Label)
{
    public string? Outcome => Outcomes.IsDefaultOrEmpty ? null : Outcomes[^1];

    public bool Answered => Answer is not null;

    /// <summary>Whether this decision may fit a floor: calibrated, answered, a catalogue question, and a known outcome.</summary>
    public bool Fittable => Tier == DecisionTier.Calibrated && Measure is not null && Question is not null
        && Label is OutcomeLabel.Positive or OutcomeLabel.Negative;
}

/// <summary>
/// Decisions read from one or more event logs, joined with their outcomes (plan §5.5). Events are read as stored; nothing
/// is re-asked.
/// </summary>
public sealed class DecisionSet
{
    private const int PageSize = 4096;

    private DecisionSet(ImmutableArray<DecisionRecord> records, ImmutableArray<OrphanOutcome> orphans,
        ImmutableArray<SessionHistory> sessions)
    {
        Records = records;
        OrphanOutcomes = orphans;
        Sessions = sessions;
    }

    public ImmutableArray<DecisionRecord> Records { get; }

    /// <summary>Outcomes whose decision is not in the same session.</summary>
    public ImmutableArray<OrphanOutcome> OrphanOutcomes { get; }

    /// <summary>Every session read, with its events, for trajectory export.</summary>
    public ImmutableArray<SessionHistory> Sessions { get; }

    /// <summary>
    /// Reads every session of each SQLite event log. A missing file is an error: opening a log creates it, so a mistyped
    /// path would otherwise read as an empty log.
    /// </summary>
    public static DecisionSet Load(IEnumerable<string> logPaths, QuestionCatalogue? catalogue = null,
        OutcomeClassifier? classifier = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logPaths);
        var sessions = new List<SessionHistory>();
        foreach (var path in logPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException($"No event log at {full}.", full);
            var log = new SqliteEventLog(full);
            foreach (var session in log.ListSessions(cancellationToken))
                sessions.Add(new SessionHistory(full, session, [.. ReadAll(log, session, cancellationToken)]));
        }
        return FromSessions(sessions, catalogue, classifier);
    }

    public static DecisionSet FromSessions(IEnumerable<SessionHistory> sessions, QuestionCatalogue? catalogue = null,
        OutcomeClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        catalogue ??= QuestionCatalogue.Default;
        classifier ??= OutcomeClassifier.Default;
        var records = ImmutableArray.CreateBuilder<DecisionRecord>();
        var orphans = ImmutableArray.CreateBuilder<OrphanOutcome>();
        var histories = sessions.ToImmutableArray();
        foreach (var history in histories)
        {
            // The first decision keeps an ID, as in SessionProjection, so a later outcome cannot attach to the wrong answer.
            var decisions = new Dictionary<string, StoredEvent>(StringComparer.Ordinal);
            var outcomes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var item in history.Events)
            {
                switch (item.Payload)
                {
                    case Decision decision:
                        decisions.TryAdd(decision.DecisionId, item);
                        break;
                    case DecisionOutcomeRecorded outcome when decisions.ContainsKey(outcome.DecisionId):
                        if (!outcomes.TryGetValue(outcome.DecisionId, out var list))
                            outcomes[outcome.DecisionId] = list = [];
                        list.Add(outcome.Outcome);
                        break;
                    case DecisionOutcomeRecorded outcome:
                        orphans.Add(new OrphanOutcome(history.Source, history.SessionId, item.Sequence, outcome.DecisionId, outcome.Outcome));
                        break;
                }
            }
            foreach (var item in decisions.Values.OrderBy(item => item.Sequence))
            {
                var recorded = outcomes.TryGetValue(((Decision)item.Payload).DecisionId, out var list) ? [.. list] : ImmutableArray<string>.Empty;
                records.Add(Build(history.Source, item, recorded, catalogue, classifier));
            }
        }
        return new DecisionSet(records.ToImmutable(), orphans.ToImmutable(), histories);
    }

    /// <summary>The tier a recorded decider name stands for. Every degraded answer, including a fallback, is uncalibrated.</summary>
    public static DecisionTier TierOf(string? decider) =>
        decider is null ? DecisionTier.Unknown
        : decider == DegradedDecider.DeciderName || decider.StartsWith(DegradedDecider.DeciderName + " ", StringComparison.Ordinal)
            ? DecisionTier.Degraded
            : DecisionTier.Calibrated;

    /// <summary>
    /// The option names in the order the decider saw them: as recorded on the event, else the catalogue's fixed options,
    /// else (a caller's shortlist written before C-021) the stored names in ordinal order. The stored map is unordered,
    /// so score levels must never come from it.
    /// </summary>
    public static ImmutableArray<string> OptionOrder(Decision decision, CatalogueQuestion? question)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Question is { Options.IsDefaultOrEmpty: false } asked)
            return [.. asked.Options.Select(option => option.Name)];
        if (question is not null && question.Kind == QuestionKind.Noul)
            return [DecisionQuestion.Yes, DecisionQuestion.No];
        if (question is { HasFixedOptions: true })
            return [.. question.Options.Select(option => option.Name)];
        return [.. decision.Probabilities.Keys.Order(StringComparer.Ordinal)];
    }

    private static DecisionRecord Build(string source, StoredEvent item, ImmutableArray<string> outcomes,
        QuestionCatalogue catalogue, OutcomeClassifier classifier)
    {
        var decision = (Decision)item.Payload;
        var separator = decision.QuestionId.IndexOf(QuestionCatalogue.KeySeparator, StringComparison.Ordinal);
        var questionId = separator < 0 ? decision.QuestionId : decision.QuestionId[..separator];
        var instance = separator < 0 ? null : decision.QuestionId[(separator + 1)..];
        var question = catalogue.TryResolve(decision.QuestionId, out var row) ? row : null;
        var tier = TierOf(decision.Decider);
        var answer = question is null ? null : Rebuild(decision, question, tier);
        var measure = answer is null ? null : DecisionPolicy.MeasureOf(question!, answer);
        var label = classifier.Classify(outcomes.IsEmpty ? null : outcomes[^1]);
        return new DecisionRecord(source, item.SessionId, item.Sequence, item.RecordedAt, decision, decision.QuestionId,
            questionId, instance, question, tier, answer, measure, ActedOn(decision, question, measure), outcomes, label);
    }

    // An empty map is a question the decider did not answer (a gate in the degraded tier). A map that does not fit the
    // question's options or sum to one is treated the same way, as the fabric treats a malformed answer.
    private static DecisionAnswer? Rebuild(Decision decision, CatalogueQuestion question, DecisionTier tier)
    {
        if (decision.Probabilities.IsEmpty)
            return null;
        var options = OptionOrder(decision, question);
        if (options.Length != decision.Probabilities.Count || options.Any(option => !decision.Probabilities.ContainsKey(option)))
            return null;
        var answer = new DecisionAnswer(question.Kind, options, [.. options.Select(option => decision.Probabilities[option])],
            Calibrated: tier == DecisionTier.Calibrated);
        var sum = answer.Probabilities.Sum();
        return answer.Probabilities.All(p => double.IsFinite(p) && p >= 0) && Math.Abs(sum - 1) <= 1e-3 ? answer : null;
    }

    // Acting is not read from the action name alone: for risk, "ask" is both the act for levels 3–4 and the below-floor
    // action. For every other row the act differs from the below-floor action, which also covers a missing condition
    // (cleared, but the below-floor action was taken) and degraded embedding choices (compared with the margin, not the floor).
    private static bool ActedOn(Decision decision, CatalogueQuestion? question, double? measure)
    {
        if (question is null || measure is null)
            return false;
        return question.Measure == FloorMeasure.RiskLevel ? measure >= decision.Threshold : decision.Action != question.BelowFloor;
    }

    private static IEnumerable<StoredEvent> ReadAll(SqliteEventLog log, string sessionId, CancellationToken cancellationToken)
    {
        var head = log.GetHead(sessionId, cancellationToken);
        for (long after = 0; after < head;)
        {
            var page = log.Read(sessionId, after, head, PageSize, cancellationToken);
            if (page.Count == 0)
                yield break;
            foreach (var item in page)
                yield return item;
            after = page[^1].Sequence;
        }
    }
}

/// <summary>A session's events as read from one log.</summary>
public sealed record SessionHistory(string Source, string SessionId, ImmutableArray<StoredEvent> Events);

/// <summary>An outcome whose decision was not found earlier in its session.</summary>
public sealed record OrphanOutcome(string Source, string SessionId, long Sequence, string DecisionId, string Outcome);

internal static class CalibrationJson
{
    public static readonly JsonWriterOptions Writer = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonWriterOptions Line = Writer with { Indented = false };
}
