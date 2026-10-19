using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Fabric;

namespace Corvid.Evals.Calibration;

/// <summary>One reliability bin: calibrated, labelled decisions whose normalized measure falls in [Lower, Upper).</summary>
/// <param name="MeanMeasure">Mean of the raw measure (an expected score is not normalized here), or null when empty.</param>
/// <param name="PositiveRate">Observed share of positive outcomes, or null when empty.</param>
public sealed record ReliabilityBin(double Lower, double Upper, int Count, double? MeanMeasure, double? PositiveRate);

/// <summary>Rates for one question within one tier group.</summary>
/// <param name="ActedRate">Share of decisions acted on.</param>
/// <param name="EscalationRate">Share that took the below-floor action: handed to the generator, the user or review.</param>
/// <param name="OutcomeCoverage">Share with a positive or negative outcome.</param>
/// <param name="PrecisionWhenActed">Positive share of the labelled decisions that were acted on, or null when there are none.</param>
public sealed record QuestionStats(
    string QuestionId,
    QuestionRole? Role,
    int Decisions,
    int Answered,
    int Acted,
    int WithOutcome,
    int Positive,
    int Negative,
    int Unlabelled,
    double ActedRate,
    double EscalationRate,
    double OutcomeCoverage,
    double? PrecisionWhenActed,
    ImmutableArray<ReliabilityBin> Reliability);

/// <summary>
/// The calibration report (plan §5.5): per question, reliability bins, acted and escalation rates and outcome coverage for
/// calibrated decisions; the same rates for uncalibrated ones (degraded tier and pre-C-004a decisions) in their own section,
/// which fits nothing; and, when floors were fitted, the fit per question.
/// </summary>
public sealed record CalibrationReport(
    ImmutableArray<string> Sources,
    int Sessions,
    int Decisions,
    ImmutableSortedDictionary<string, int> Tiers,
    ImmutableSortedDictionary<string, int> Deciders,
    ImmutableSortedDictionary<string, int> FloorVersions,
    ImmutableArray<QuestionStats> Calibrated,
    ImmutableArray<QuestionStats> Uncalibrated,
    ImmutableSortedDictionary<string, int> UnknownQuestions,
    ImmutableSortedDictionary<string, int> UnlabelledOutcomes,
    int OrphanOutcomes,
    FitResult? Fit)
{
    public const string Format = "corvid-calibration-report";
    public const int FormatVersion = 1;
    public const int Bins = 10;

    public static CalibrationReport Build(DecisionSet decisions, FitResult? fit = null, QuestionCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        catalogue ??= QuestionCatalogue.Default;
        var records = decisions.Records;
        var known = records.Where(record => record.Question is not null).ToArray();
        return new CalibrationReport(
            [.. decisions.Sessions.Select(session => session.Source).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            decisions.Sessions.Length,
            records.Length,
            Count(records.Select(record => Wire(record.Tier))),
            Count(records.Select(record => record.Event.Decider ?? "(none)")),
            Count(records.Select(record => record.Event.CatalogueVersion ?? "(none)")),
            Stats(known.Where(record => record.Tier == DecisionTier.Calibrated), withBins: true),
            Stats(known.Where(record => record.Tier != DecisionTier.Calibrated), withBins: false),
            Count(records.Where(record => record.Question is null).Select(record => record.QuestionId)),
            Count(records.Where(record => record.Label == OutcomeLabel.Unlabelled).Select(record => record.Outcome!)),
            decisions.OrphanOutcomes.Length,
            fit);
    }

    private static ImmutableArray<QuestionStats> Stats(IEnumerable<DecisionRecord> records, bool withBins) =>
    [
        .. records.GroupBy(record => record.QuestionId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => QuestionStatsOf(group.Key, [.. group], withBins)),
    ];

    private static QuestionStats QuestionStatsOf(string questionId, DecisionRecord[] records, bool withBins)
    {
        var question = records[0].Question!;
        int total = records.Length, acted = records.Count(record => record.Acted);
        int positive = records.Count(record => record.Label == OutcomeLabel.Positive);
        int negative = records.Count(record => record.Label == OutcomeLabel.Negative);
        var actedLabelled = records.Where(record => record.Acted && record.Label is OutcomeLabel.Positive or OutcomeLabel.Negative).ToArray();
        return new QuestionStats(questionId, question.Role, total,
            records.Count(record => record.Answered), acted,
            records.Count(record => record.Label != OutcomeLabel.None), positive, negative,
            records.Count(record => record.Label == OutcomeLabel.Unlabelled),
            acted / (double)total, (total - acted) / (double)total, (positive + negative) / (double)total,
            actedLabelled.Length == 0 ? null : actedLabelled.Count(record => record.Label == OutcomeLabel.Positive) / (double)actedLabelled.Length,
            withBins ? Reliability(question, records.Where(record => record.Fittable)) : []);
    }

    // Measures are normalized to [0, 1] for binning: an expected score divides by its top level.
    private static ImmutableArray<ReliabilityBin> Reliability(CatalogueQuestion question, IEnumerable<DecisionRecord> samples)
    {
        var scale = question.Measure == FloorMeasure.ExpectedScore ? question.Options.Length - 1 : 1.0;
        var bins = Enumerable.Range(0, Bins).Select(_ => new List<DecisionRecord>()).ToArray();
        foreach (var sample in samples)
            bins[Math.Clamp((int)Math.Floor(sample.Measure!.Value / scale * Bins), 0, Bins - 1)].Add(sample);
        return
        [
            .. bins.Select((bin, index) => new ReliabilityBin(index / (double)Bins, (index + 1) / (double)Bins, bin.Count,
                bin.Count == 0 ? null : bin.Average(sample => sample.Measure!.Value),
                bin.Count == 0 ? null : bin.Count(sample => sample.Label == OutcomeLabel.Positive) / (double)bin.Count)),
        ];
    }

    private static ImmutableSortedDictionary<string, int> Count(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal)
            .ToImmutableSortedDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    internal static string Wire(DecisionTier tier) => tier switch
    {
        DecisionTier.Calibrated => "calibrated",
        DecisionTier.Degraded => "degraded",
        _ => "unknown",
    };

    internal static string Wire(FitStatus status) => status switch
    {
        FitStatus.Fitted => "fitted",
        FitStatus.InsufficientData => "insufficient_data",
        _ => "target_unreachable",
    };

    internal static string Wire(QuestionRole? role) => role switch
    {
        QuestionRole.Gate => "gate",
        QuestionRole.Routing => "routing",
        _ => "unknown",
    };

    public string ToText()
    {
        var text = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        text.AppendLine(c, $"Calibration report: {Decisions} decisions in {Sessions} sessions from {Sources.Length} log(s)");
        text.AppendLine(c, $"  tiers: {Join(Tiers)}");
        text.AppendLine(c, $"  deciders: {Join(Deciders)}");
        text.AppendLine(c, $"  floor versions: {Join(FloorVersions)}");
        if (!UnknownQuestions.IsEmpty)
            text.AppendLine(c, $"  questions not in the catalogue (ignored): {Join(UnknownQuestions)}");
        if (!UnlabelledOutcomes.IsEmpty)
            text.AppendLine(c, $"  outcomes with no label (not used for fitting): {Join(UnlabelledOutcomes)}");
        if (OrphanOutcomes > 0)
            text.AppendLine(c, $"  outcomes for unknown decisions: {OrphanOutcomes}");

        text.AppendLine().AppendLine("Calibrated decisions");
        if (Calibrated.IsEmpty)
            text.AppendLine("  none: every decision came from the degraded tier or predates C-004a, so no floor can be fitted.");
        foreach (var stats in Calibrated)
        {
            AppendStats(text, stats);
            foreach (var bin in stats.Reliability.Where(bin => bin.Count > 0))
                text.AppendLine(c, $"      [{bin.Lower:0.0}, {bin.Upper:0.0}) n={bin.Count} mean={bin.MeanMeasure:0.000} positive={bin.PositiveRate:0.000}");
        }

        text.AppendLine().AppendLine("Uncalibrated decisions (degraded tier and pre-C-004a; reported only, never fitted)");
        if (Uncalibrated.IsEmpty)
            text.AppendLine("  none");
        foreach (var stats in Uncalibrated)
            AppendStats(text, stats);

        if (Fit is not null)
        {
            text.AppendLine().AppendLine(c, $"Floors {Fit.Current.Version} -> {Fit.Floors.Version}");
            foreach (var fit in Fit.Questions)
            {
                var change = fit.Changed
                    ? string.Create(c, $"{fit.CurrentFloor:0.####} -> {fit.Floor:0.####}")
                    : string.Create(c, $"{fit.Floor:0.####} (kept)");
                text.Append(c, $"  {fit.QuestionId,-18} {Wire(fit.Role),-7} target {fit.Target:0.00}  {Wire(fit.Status),-18} {change}  samples {fit.Samples} (+{fit.Positives})");
                if (fit.Excluded > 0)
                    text.Append(c, $", {fit.Excluded} uncalibrated excluded");
                if (fit.Samples > 0)
                    text.Append(c, $"  precision {Rate(fit.AtFloor.Precision)} coverage {fit.AtFloor.Coverage:0.000}");
                text.AppendLine();
            }
        }
        return text.ToString();
    }

    private static void AppendStats(StringBuilder text, QuestionStats stats) =>
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  {stats.QuestionId,-18} {Wire(stats.Role),-7} n={stats.Decisions} answered={stats.Answered} acted={stats.ActedRate:0.000} escalated={stats.EscalationRate:0.000} coverage={stats.OutcomeCoverage:0.000} (+{stats.Positive} -{stats.Negative} ?{stats.Unlabelled}) precision_when_acted={Rate(stats.PrecisionWhenActed)}");

    private static string Rate(double? value) => value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "n/a";

    private static string Join(ImmutableSortedDictionary<string, int> counts) =>
        counts.IsEmpty ? "none" : string.Join(", ", counts.Select(pair => $"{pair.Key} {pair.Value}"));

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, CalibrationJson.Writer))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("format_version", FormatVersion);
            writer.WriteStartArray("sources");
            foreach (var source in Sources)
                writer.WriteStringValue(source);
            writer.WriteEndArray();
            writer.WriteNumber("sessions", Sessions);
            writer.WriteNumber("decisions", Decisions);
            WriteCounts(writer, "tiers", Tiers);
            WriteCounts(writer, "deciders", Deciders);
            WriteCounts(writer, "floor_versions", FloorVersions);
            WriteCounts(writer, "unknown_questions", UnknownQuestions);
            WriteCounts(writer, "unlabelled_outcomes", UnlabelledOutcomes);
            writer.WriteNumber("orphan_outcomes", OrphanOutcomes);
            WriteStats(writer, "calibrated", Calibrated);
            WriteStats(writer, "uncalibrated", Uncalibrated);
            if (Fit is null)
            {
                writer.WriteNull("fit");
            }
            else
            {
                writer.WriteStartObject("fit");
                writer.WriteString("from_version", Fit.Current.Version);
                writer.WriteString("version", Fit.Floors.Version);
                writer.WriteStartArray("questions");
                foreach (var fit in Fit.Questions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("question", fit.QuestionId);
                    writer.WriteString("role", Wire(fit.Role));
                    writer.WriteNumber("target", fit.Target);
                    writer.WriteString("status", Wire(fit.Status));
                    writer.WriteNumber("current_floor", fit.CurrentFloor);
                    writer.WriteNumber("floor", fit.Floor);
                    writer.WriteBoolean("changed", fit.Changed);
                    writer.WriteNumber("samples", fit.Samples);
                    writer.WriteNumber("positives", fit.Positives);
                    writer.WriteNumber("uncalibrated_excluded", fit.Excluded);
                    WritePoint(writer, "at_current", fit.AtCurrent);
                    WritePoint(writer, "at_floor", fit.AtFloor);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteStats(Utf8JsonWriter writer, string name, ImmutableArray<QuestionStats> all)
    {
        writer.WriteStartArray(name);
        foreach (var stats in all)
        {
            writer.WriteStartObject();
            writer.WriteString("question", stats.QuestionId);
            writer.WriteString("role", Wire(stats.Role));
            writer.WriteNumber("decisions", stats.Decisions);
            writer.WriteNumber("answered", stats.Answered);
            writer.WriteNumber("acted", stats.Acted);
            writer.WriteNumber("with_outcome", stats.WithOutcome);
            writer.WriteNumber("positive", stats.Positive);
            writer.WriteNumber("negative", stats.Negative);
            writer.WriteNumber("unlabelled", stats.Unlabelled);
            writer.WriteNumber("acted_rate", stats.ActedRate);
            writer.WriteNumber("escalation_rate", stats.EscalationRate);
            writer.WriteNumber("outcome_coverage", stats.OutcomeCoverage);
            WriteNullable(writer, "precision_when_acted", stats.PrecisionWhenActed);
            if (!stats.Reliability.IsDefaultOrEmpty)
            {
                writer.WriteStartArray("reliability");
                foreach (var bin in stats.Reliability)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("lower", bin.Lower);
                    writer.WriteNumber("upper", bin.Upper);
                    writer.WriteNumber("count", bin.Count);
                    WriteNullable(writer, "mean_measure", bin.MeanMeasure);
                    WriteNullable(writer, "positive_rate", bin.PositiveRate);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, FloorPoint point)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("floor", point.Floor);
        writer.WriteNumber("support", point.Support);
        WriteNullable(writer, "precision", point.Precision);
        writer.WriteNumber("coverage", point.Coverage);
        writer.WriteEndObject();
    }

    internal static void WriteNullable(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } v)
            writer.WriteNumber(name, v);
        else
            writer.WriteNull(name);
    }

    private static void WriteCounts(Utf8JsonWriter writer, string name, ImmutableSortedDictionary<string, int> counts)
    {
        writer.WriteStartObject(name);
        foreach (var (key, count) in counts)
            writer.WriteNumber(key, count);
        writer.WriteEndObject();
    }
}
