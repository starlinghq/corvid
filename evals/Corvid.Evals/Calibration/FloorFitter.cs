using System.Collections.Immutable;
using Corvid.Fabric;

namespace Corvid.Evals.Calibration;

/// <param name="Version">The version of the floors file to write. It must differ from the current one: events record it.</param>
/// <param name="GatePrecision">Target precision for gates (plan §5.5: 0.9).</param>
/// <param name="RoutingPrecision">Target precision for routing questions (plan §5.5: 0.8).</param>
/// <param name="MinSupport">Fewest labelled decisions at or above a floor for it to count as evidence.</param>
public sealed record FitOptions(string Version, double GatePrecision = 0.9, double RoutingPrecision = 0.8, int MinSupport = 20)
{
    public FitOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Version);
        if (!double.IsFinite(GatePrecision) || GatePrecision is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(GatePrecision), "Precision targets are in (0, 1].");
        if (!double.IsFinite(RoutingPrecision) || RoutingPrecision is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(RoutingPrecision), "Precision targets are in (0, 1].");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinSupport);
        return this;
    }
}

public enum FitStatus
{
    /// <summary>A floor was fitted; it may equal the current one.</summary>
    Fitted,
    /// <summary>Fewer calibrated, labelled decisions than the minimum support. The current floor is kept.</summary>
    InsufficientData,
    /// <summary>No floor with enough support reaches the target. The current floor is kept and the report says so.</summary>
    TargetUnreachable,
}

/// <summary>Precision and coverage of acting at one floor, over the calibrated, labelled decisions of a question.</summary>
/// <param name="Support">Decisions at or above the floor.</param>
/// <param name="Precision">Positive share of those, or null when there are none.</param>
/// <param name="Coverage">Share of the question's labelled decisions at or above the floor.</param>
public sealed record FloorPoint(double Floor, int Support, double? Precision, double Coverage);

/// <param name="Samples">Calibrated, answered decisions with a positive or negative outcome: the only fitting evidence.</param>
/// <param name="Excluded">Decisions with an answer and a label that were left out because they are uncalibrated.</param>
public sealed record QuestionFit(
    string QuestionId,
    QuestionRole Role,
    FloorMeasure Measure,
    double Target,
    FitStatus Status,
    double CurrentFloor,
    double Floor,
    int Samples,
    int Positives,
    int Excluded,
    FloorPoint AtCurrent,
    FloorPoint AtFloor)
{
    public bool Changed => Floor != CurrentFloor;
}

public sealed record FitResult(FloorConfiguration Current, FloorConfiguration Floors, ImmutableArray<QuestionFit> Questions);

/// <summary>
/// Fits per-question floors that hold a target precision (plan §5.5). For each question the floor is the lowest observed
/// measure at which the decisions at or above it have at least <see cref="FitOptions.MinSupport"/> members and a positive
/// share at or above the target. Only calibrated answers are evidence: degraded and pre-C-004a decisions never fit a
/// floor, gate or routing, because the floors apply to a calibrated decider's confidence. Every labelled decision counts,
/// acted on or not (a staged memory write that the user approves is evidence for a lower floor). That assumes an outcome
/// judges the decider's answer whichever action followed, which holds for the outcomes written today (C-008's review).
/// </summary>
public static class FloorFitter
{
    private const double Tolerance = 1e-12;

    public static FitResult Fit(DecisionSet decisions, FloorConfiguration current, FitOptions options,
        QuestionCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        catalogue ??= QuestionCatalogue.Default;
        current.Validate(catalogue);
        if (options.Version == current.Version)
            throw new ArgumentException($"The new floors need a version other than the current '{current.Version}'.", nameof(options));

        var fits = ImmutableArray.CreateBuilder<QuestionFit>();
        var floors = current.Floors.ToBuilder();
        foreach (var question in catalogue.Questions.OrderBy(question => question.Id, StringComparer.Ordinal))
        {
            var records = decisions.Records.Where(record => record.Question?.Id == question.Id).ToArray();
            var samples = records.Where(record => record.Fittable).Select(record => (record.Measure!.Value, record.Label == OutcomeLabel.Positive)).ToArray();
            var excluded = records.Count(record => record.Tier != DecisionTier.Calibrated && record.Measure is not null
                && record.Label is OutcomeLabel.Positive or OutcomeLabel.Negative);
            var fit = FitOne(question, samples, current.FloorOf(question.Id), options) with { Excluded = excluded };
            floors[question.Id] = fit.Floor;
            fits.Add(fit);
        }

        // Round-trip through the parser so the file written is exactly one the fabric will load.
        var fitted = FloorConfiguration.Parse(
            (current with { Version = options.Version, Floors = floors.ToImmutable() }).ToJson(), catalogue);
        return new FitResult(current, fitted, fits.ToImmutable());
    }

    /// <summary>Precision and coverage of acting at <paramref name="floor"/> over (measure, positive) samples.</summary>
    public static FloorPoint At(double floor, IReadOnlyCollection<(double Measure, bool Positive)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var above = samples.Where(sample => sample.Measure >= floor).ToArray();
        return new FloorPoint(floor, above.Length,
            above.Length == 0 ? null : above.Count(sample => sample.Positive) / (double)above.Length,
            samples.Count == 0 ? 0 : above.Length / (double)samples.Count);
    }

    private static QuestionFit FitOne(CatalogueQuestion question, (double Measure, bool Positive)[] samples, double current,
        FitOptions options)
    {
        var target = question.Role == QuestionRole.Gate ? options.GatePrecision : options.RoutingPrecision;
        var atCurrent = At(current, samples);
        var positives = samples.Count(sample => sample.Positive);

        QuestionFit Result(FitStatus status, double floor) => new(question.Id, question.Role, question.Measure, target, status,
            current, floor, samples.Length, positives, 0, atCurrent, floor == current ? atCurrent : At(floor, samples));

        if (samples.Length < options.MinSupport)
            return Result(FitStatus.InsufficientData, current);

        // Walk thresholds from the highest measure down; support only grows, so record the lowest that holds the target.
        double? best = null;
        int support = 0, hits = 0;
        foreach (var group in samples.GroupBy(sample => sample.Measure).OrderByDescending(group => group.Key))
        {
            support += group.Count();
            hits += group.Count(sample => sample.Positive);
            if (support >= options.MinSupport && hits / (double)support + Tolerance >= target)
                best = group.Key;
        }
        return best is { } floor ? Result(FitStatus.Fitted, floor) : Result(FitStatus.TargetUnreachable, current);
    }
}
