using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric;

/// <summary>What the policy did with one answer.</summary>
/// <param name="Key">The request key, for example <c>tool</c> or <c>clear_results/call-7</c>.</param>
/// <param name="Threshold">The value the answer was compared with: the floor, or the degraded margin for an embedding choice.</param>
/// <param name="Acted">True when the answer cleared its threshold and every required condition held.</param>
/// <param name="Option">The chosen option or score level when the policy acted on one.</param>
/// <param name="Reason">Why the action was chosen, for logs and <c>/insights</c>. Not persisted.</param>
public sealed record PolicyVerdict(string Key, string QuestionId, string Action, double Threshold, bool Acted, string? Option, string Reason);

/// <summary>
/// Applies floors to answers (plan §5.3). Rules: an uncalibrated answer never passes a gate, and never diverts the turn
/// (<see cref="CatalogueQuestion.BlocksTurn"/>) unless <see cref="FloorConfiguration.DegradedTurnBlocking"/> allows it; a
/// degraded choice acts only on a similarity margin above the configured one; a heuristic score is logged but never acted
/// on; a missing answer takes the below-floor action; a question with required conditions acts only when the caller
/// supplies all of them.
/// </summary>
public sealed class DecisionPolicy
{
    public DecisionPolicy(QuestionCatalogue catalogue, FloorConfiguration floors)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(floors);
        Catalogue = catalogue;
        Floors = floors.Validate(catalogue);
    }

    public static DecisionPolicy Default { get; } = new(QuestionCatalogue.Default, FloorConfiguration.Default);

    public QuestionCatalogue Catalogue { get; }

    public FloorConfiguration Floors { get; }

    public PolicyVerdict Evaluate(string key, DecisionAnswer? answer, IReadOnlySet<string>? conditions = null)
    {
        var question = Catalogue.Resolve(key);
        var floor = Floors.FloorOf(question.Id);

        PolicyVerdict Below(double threshold, string reason) =>
            new(key, question.Id, question.BelowFloor, threshold, false, null, reason);

        if (answer is null)
            return Below(floor, "no answer");
        if (answer.Kind != question.Kind)
            return Below(floor, $"answer is {answer.Kind}, question is {question.Kind}");

        double threshold = floor;
        bool clears;
        if (!answer.Calibrated)
        {
            if (question.Role == QuestionRole.Gate)
                return Below(floor, "gate without a calibrated answer");
            if (question.BlocksTurn && !Floors.DegradedTurnBlocking)
                return Below(floor, "an uncalibrated answer does not divert the turn");
            switch (question.Degraded)
            {
                case DegradedRule.EmbeddingMargin when answer.Margin is { } margin:
                    threshold = Floors.DegradedChoiceMargin;
                    clears = margin > threshold;
                    break;
                case DegradedRule.GeneratorYesNo:
                    clears = Clears(question, answer, floor);
                    break;
                case DegradedRule.Heuristic:
                    return Below(floor, "heuristic answer; act manually");
                default:
                    return Below(floor, "uncalibrated answer");
            }
        }
        else
        {
            clears = Clears(question, answer, floor);
        }

        if (!clears)
            return Below(threshold, "below threshold");
        var missing = question.Requires.Where(condition => conditions is null || !conditions.Contains(condition)).ToArray();
        if (missing.Length > 0)
            return Below(threshold, "missing condition " + string.Join(", ", missing));

        return question.Measure switch
        {
            FloorMeasure.RiskLevel => Risk(key, question, answer, threshold, conditions),
            _ when question.Kind == QuestionKind.Choice =>
                new(key, question.Id, $"{question.Act}:{answer.Choice}", threshold, true, answer.Choice, "above threshold"),
            _ => new(key, question.Id, question.Act, threshold, true, question.Kind == QuestionKind.Score ? answer.Choice : null,
                "above threshold"),
        };
    }

    /// <summary>
    /// Whether an answer's measure reaches its question's floor, ignoring conditions and the degraded rules: the test the
    /// two-tier decider (§5.5) applies to a calibrated gate answer before asking the second tier.
    /// </summary>
    public bool ClearsFloor(string key, DecisionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var question = Catalogue.Resolve(key);
        return answer.Kind == question.Kind && Clears(question, answer, Floors.FloorOf(question.Id));
    }

    private static bool Clears(CatalogueQuestion question, DecisionAnswer answer, double floor) =>
        MeasureOf(question, answer) is { } measure && measure >= floor;

    /// <summary>
    /// The value a question's floor is compared with (<see cref="CatalogueQuestion.Measure"/>): choice confidence, P(yes)
    /// or the expected score level. Calibration fits floors against this value, so it is the policy's own definition.
    /// </summary>
    public static double? MeasureOf(CatalogueQuestion question, DecisionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        return question.Measure switch
        {
            FloorMeasure.Confidence or FloorMeasure.RiskLevel => answer.Confidence,
            FloorMeasure.YesProbability => answer.ProbabilityOf(DecisionQuestion.Yes),
            FloorMeasure.ExpectedScore => answer.ExpectedScore,
            _ => null,
        };
    }

    // Plan §5.3: levels 3–4 always ask; 0–1 may auto-run in permissive modes; otherwise the approval mode decides (C-007b).
    // A low score never overrides the hardline deny list or the sandbox; those run outside the fabric. Acted is true even
    // when the action is "ask": the answer cleared its floor, and asking is what a high level calls for.
    private static PolicyVerdict Risk(string key, CatalogueQuestion question, DecisionAnswer answer, double threshold,
        IReadOnlySet<string>? conditions)
    {
        var level = answer.ChoiceIndex;
        var action = level >= 3 ? FabricActions.Ask
            : level <= 1 && conditions?.Contains(FabricConditions.PermissiveMode) == true ? FabricActions.AutoRun
            : FabricActions.ApprovalMode;
        return new(key, question.Id, action, threshold, true, answer.Choice, $"risk level {level}");
    }
}
