using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Fabric;

namespace Corvid.Evals.Calibration;

/// <summary>A past decision whose action a candidate floors file would have changed.</summary>
/// <param name="ConditionsUnverified">
/// The decision now acts but its question requires caller conditions, which the log does not record; the condition may
/// still have held the action back.
/// </param>
public sealed record ReplayChange(
    DecisionRecord Decision,
    double Measure,
    double OldThreshold,
    double NewThreshold,
    string OldAction,
    string NewAction,
    bool NowActs,
    bool ConditionsUnverified);

/// <summary>Per question: how many answered decisions were replayed and what would change.</summary>
/// <param name="NotReplayable">
/// Decisions whose threshold changed but whose comparison cannot be redone from the log: degraded embedding choices (the
/// margin itself is not recorded) and pre-C-004a decisions with no recorded tier.
/// </param>
public sealed record ReplayQuestion(string QuestionId, int Replayed, int Changed, int NowAct, int NowBelow,
    int ChangedPositive, int ChangedNegative, int NotReplayable);

public sealed record ReplayResult(string CandidateVersion, ImmutableArray<ReplayChange> Changes,
    ImmutableArray<ReplayQuestion> Questions)
{
    public const string Format = "corvid-floor-replay";
    public const int FormatVersion = 1;

    public string ToText()
    {
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.AppendLine(c, $"Replay against floors {CandidateVersion}: {Changes.Length} decision(s) would change");
        foreach (var question in Questions.Where(question => question.Changed > 0 || question.NotReplayable > 0))
        {
            text.Append(c, $"  {question.QuestionId,-18} replayed {question.Replayed}, changed {question.Changed} (now act {question.NowAct}, now below {question.NowBelow}; outcomes +{question.ChangedPositive} -{question.ChangedNegative})");
            if (question.NotReplayable > 0)
                text.Append(c, $", not replayable {question.NotReplayable}");
            text.AppendLine();
        }
        foreach (var change in Changes)
        {
            text.Append(c, $"  {change.Decision.SessionId}#{change.Decision.Sequence} {change.Decision.Key}: {change.Measure:0.####} vs {change.OldThreshold:0.####} -> {change.NewThreshold:0.####}: {change.OldAction} -> {change.NewAction}");
            text.Append(c, $" (outcome {change.Decision.Outcome ?? "none"})");
            if (change.ConditionsUnverified)
                text.Append(" [conditions unverified]");
            text.AppendLine();
        }
        return text.ToString();
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CalibrationJson.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("format_version", FormatVersion);
            writer.WriteString("candidate_version", CandidateVersion);
            writer.WriteStartArray("questions");
            foreach (var question in Questions)
            {
                writer.WriteStartObject();
                writer.WriteString("question", question.QuestionId);
                writer.WriteNumber("replayed", question.Replayed);
                writer.WriteNumber("changed", question.Changed);
                writer.WriteNumber("now_act", question.NowAct);
                writer.WriteNumber("now_below", question.NowBelow);
                writer.WriteNumber("changed_positive", question.ChangedPositive);
                writer.WriteNumber("changed_negative", question.ChangedNegative);
                writer.WriteNumber("not_replayable", question.NotReplayable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("changes");
            foreach (var change in Changes)
            {
                var decision = change.Decision;
                writer.WriteStartObject();
                writer.WriteString("source", decision.Source);
                writer.WriteString("session_id", decision.SessionId);
                writer.WriteNumber("sequence", decision.Sequence);
                writer.WriteString("decision_id", decision.Event.DecisionId);
                writer.WriteString("question", decision.QuestionId);
                if (decision.Instance is null)
                    writer.WriteNull("instance");
                else
                    writer.WriteString("instance", decision.Instance);
                writer.WriteString("tier", CalibrationReport.Wire(decision.Tier));
                writer.WriteNumber("measure", change.Measure);
                writer.WriteNumber("old_threshold", change.OldThreshold);
                writer.WriteNumber("new_threshold", change.NewThreshold);
                writer.WriteString("old_action", change.OldAction);
                writer.WriteString("new_action", change.NewAction);
                writer.WriteBoolean("now_acts", change.NowActs);
                writer.WriteBoolean("conditions_unverified", change.ConditionsUnverified);
                if (decision.Outcome is null)
                    writer.WriteNull("outcome");
                else
                    writer.WriteString("outcome", decision.Outcome);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Shows which past decisions a candidate floors file would have changed (plan §3.2, §5.5), before it ships. Each answered
/// decision's stored measure is compared with the threshold it was held to (recorded on the event) and with the candidate
/// floor. The policy itself is not re-run: the caller's conditions are not in the log, and re-running would read every
/// missing condition as a failed one. A decision that cleared its floor but was held back by a condition stays held back.
/// </summary>
public static class FloorReplay
{
    public static ReplayResult Run(DecisionSet decisions, FloorConfiguration candidate, QuestionCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(candidate);
        catalogue ??= QuestionCatalogue.Default;
        candidate.Validate(catalogue);

        var changes = ImmutableArray.CreateBuilder<ReplayChange>();
        var questions = new SortedDictionary<string, (int Replayed, int NotReplayable)>(StringComparer.Ordinal);
        foreach (var record in decisions.Records)
        {
            if (record.Question is not { } question || record.Measure is not { } measure)
                continue;
            var counts = questions.GetValueOrDefault(question.Id);
            var outcome = Replay(record, question, measure, candidate);
            if (outcome is Verdict.NotReplayable)
            {
                questions[question.Id] = counts with { NotReplayable = counts.NotReplayable + 1 };
                continue;
            }
            questions[question.Id] = counts with { Replayed = counts.Replayed + 1 };
            if (outcome is Verdict.Changed changed)
                changes.Add(changed.Change);
        }

        var built = changes.ToImmutable();
        return new ReplayResult(candidate.Version, built,
        [
            .. questions.Select(pair =>
            {
                var mine = built.Where(change => change.Decision.QuestionId == pair.Key).ToArray();
                return new ReplayQuestion(pair.Key, pair.Value.Replayed, mine.Length, mine.Count(change => change.NowActs),
                    mine.Count(change => !change.NowActs), mine.Count(change => change.Decision.Label == OutcomeLabel.Positive),
                    mine.Count(change => change.Decision.Label == OutcomeLabel.Negative), pair.Value.NotReplayable);
            }),
        ]);
    }

    private abstract record Verdict
    {
        public sealed record Same : Verdict;
        public sealed record NotReplayable : Verdict;
        public sealed record Changed(ReplayChange Change) : Verdict;
    }

    private static Verdict Replay(DecisionRecord record, CatalogueQuestion question, double measure, FloorConfiguration candidate)
    {
        var decision = record.Event;
        double newThreshold;
        if (record.Tier == DecisionTier.Calibrated)
        {
            newThreshold = candidate.FloorOf(question.Id);
        }
        else
        {
            // Floors govern an uncalibrated answer only where the degraded tier compares the floor itself (generator yes/no).
            // Gates, heuristics and conservative defaults never act uncalibrated, whatever the floors say.
            switch (question.Degraded)
            {
                case DegradedRule.GeneratorYesNo when question.Role == QuestionRole.Routing && record.Tier == DecisionTier.Degraded:
                    newThreshold = candidate.FloorOf(question.Id);
                    break;
                case DegradedRule.EmbeddingMargin when record.Tier == DecisionTier.Degraded:
                    // The margin is not recorded, only the threshold it was held to.
                    return candidate.DegradedChoiceMargin == decision.Threshold ? new Verdict.Same() : new Verdict.NotReplayable();
                default:
                    if (record.Tier == DecisionTier.Unknown && candidate.FloorOf(question.Id) != decision.Threshold)
                        return new Verdict.NotReplayable();
                    return new Verdict.Same();
            }
        }

        var oldClears = measure >= decision.Threshold;
        var newClears = measure >= newThreshold;
        if (oldClears == newClears)
            return new Verdict.Same();

        // A decision that cleared but was held back by a missing condition already took the below-floor action, so
        // dropping below the new floor leaves it unchanged here.
        var newAction = newClears ? ActionOf(question, record) : question.BelowFloor;
        if (newAction == decision.Action)
            return new Verdict.Same();
        return new Verdict.Changed(new ReplayChange(record, measure, decision.Threshold, newThreshold, decision.Action, newAction,
            newClears, newClears && !question.Requires.IsEmpty));
    }

    // As DecisionPolicy names actions. For risk, a level of 0–1 is reported as approval_mode: whether the session was in a
    // permissive mode (auto_run) is not recorded.
    private static string ActionOf(CatalogueQuestion question, DecisionRecord record)
    {
        var answer = record.Answer!;
        if (question.Measure == FloorMeasure.RiskLevel)
            return answer.ChoiceIndex >= 3 ? FabricActions.Ask : FabricActions.ApprovalMode;
        return question.Kind == Abstractions.Fabric.QuestionKind.Choice ? $"{question.Act}:{answer.Choice}" : question.Act;
    }
}
