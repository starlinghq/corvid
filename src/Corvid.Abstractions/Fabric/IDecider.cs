using System.Collections.Immutable;
using System.Text.Json;

namespace Corvid.Abstractions.Fabric;

/// <summary>The shape of a typed question (plan §5.2). Kev answers all three with probabilities.</summary>
public enum QuestionKind
{
    /// <summary>Yes or no. Options are always <c>yes</c>, <c>no</c>.</summary>
    Noul,
    /// <summary>One of several named options, each with an optional description.</summary>
    Choice,
    /// <summary>Ordered levels indexed from 0; <see cref="DecisionAnswer.ExpectedScore"/> is Σ i·pᵢ.</summary>
    Score,
}

/// <summary>Text or a JSON object. Mirrors <c>KevContent</c>: Kev renders a JSON object as <c>key: value</c> lines.</summary>
public sealed record DecisionContent
{
    private DecisionContent(string? text, JsonElement? json)
    {
        Text = text;
        Json = json;
    }

    public string? Text { get; }

    public JsonElement? Json { get; }

    public static DecisionContent FromText(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new(text, null);
    }

    public static DecisionContent FromJson(JsonElement json) => new(null, json.Clone());

    public override string ToString() => Text ?? Json!.Value.GetRawText();
}

/// <param name="Description">Read by Kev as <c>name: description</c> at the option's closing token.</param>
public sealed record QuestionOption(string Name, string? Description = null)
{
    public override string ToString() => Description is null ? Name : $"{Name}: {Description}";
}

/// <summary>One named question in a <see cref="DecisionRequest"/>. Content only this question needs goes in its instructions or options.</summary>
public sealed record DecisionQuestion
{
    public const string Yes = "yes";
    public const string No = "no";

    private DecisionQuestion(QuestionKind kind, DecisionContent instructions, ImmutableArray<QuestionOption> options)
    {
        Kind = kind;
        Instructions = instructions;
        Options = options;
    }

    public QuestionKind Kind { get; }

    public DecisionContent Instructions { get; }

    public ImmutableArray<QuestionOption> Options { get; }

    public static DecisionQuestion Noul(string instructions) => Noul(DecisionContent.FromText(instructions));

    public static DecisionQuestion Noul(DecisionContent instructions) =>
        new(QuestionKind.Noul, Check(instructions), [new(Yes), new(No)]);

    public static DecisionQuestion Choice(string instructions, params IEnumerable<string> options) =>
        Choice(DecisionContent.FromText(instructions), options.Select(name => new QuestionOption(name)));

    public static DecisionQuestion Choice(DecisionContent instructions, IEnumerable<QuestionOption> options) =>
        new(QuestionKind.Choice, Check(instructions), CheckOptions(options));

    /// <param name="levels">Lowest first; the index is the level's score.</param>
    public static DecisionQuestion Score(string instructions, params IEnumerable<string> levels) =>
        Score(DecisionContent.FromText(instructions), levels.Select(name => new QuestionOption(name)));

    public static DecisionQuestion Score(DecisionContent instructions, IEnumerable<QuestionOption> levels) =>
        new(QuestionKind.Score, Check(instructions), CheckOptions(levels));

    private static DecisionContent Check(DecisionContent instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        return instructions;
    }

    private static ImmutableArray<QuestionOption> CheckOptions(IEnumerable<QuestionOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = options.ToImmutableArray();
        if (result.Length < 2)
            throw new ArgumentException("A question needs at least two options.", nameof(options));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in result)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(option?.Name, nameof(options));
            if (!names.Add(option.Name))
                throw new ArgumentException($"Duplicate option '{option.Name}'.", nameof(options));
        }
        return result;
    }
}

/// <summary>One checkpoint's call: a shared state, run once, and named questions answered against it (Kev's <c>KevRequest</c>).</summary>
/// <param name="State">The TurnState (plan §5.1), never the transcript.</param>
public sealed record DecisionRequest
{
    public DecisionRequest(JsonElement state, IEnumerable<KeyValuePair<string, DecisionQuestion>> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var map = ImmutableDictionary.CreateRange(StringComparer.Ordinal, questions);
        if (map.IsEmpty)
            throw new ArgumentException("A request needs at least one question.", nameof(questions));
        foreach (var (name, question) in map)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(questions));
            ArgumentNullException.ThrowIfNull(question, nameof(questions));
        }
        State = state.Clone();
        Questions = map;
    }

    public JsonElement State { get; }

    public ImmutableDictionary<string, DecisionQuestion> Questions { get; }
}

/// <summary>A typed answer. Probabilities follow the question's option order, so score levels keep their index.</summary>
/// <param name="Calibrated">
/// False when no calibrated model produced the answer (the degraded tier, plan §5.6). Policy never lets an
/// uncalibrated answer pass a gate.
/// </param>
/// <param name="Margin">Similarity margin between the top two options, set only by embedding-based answers.</param>
public sealed record DecisionAnswer(
    QuestionKind Kind,
    ImmutableArray<string> Options,
    ImmutableArray<double> Probabilities,
    bool Calibrated = true,
    double? Margin = null)
{
    private const double SumTolerance = 1e-3;

    /// <summary>The most probable option; ties go to the earlier option.</summary>
    public string Choice => Options[IndexOfMax()];

    /// <summary>The index of <see cref="Choice"/>, which is the level for a score.</summary>
    public int ChoiceIndex => IndexOfMax();

    /// <summary>For a Noul question, P(yes) ≥ 0.5.</summary>
    public bool Yes => ProbabilityOf(DecisionQuestion.Yes) >= 0.5;

    /// <summary>Kev's choice confidence <c>(p_max − 1/K)/(1 − 1/K)</c>: 0 for a uniform answer, 1 for certainty.</summary>
    public double Confidence
    {
        get
        {
            var uniform = 1.0 / Options.Length;
            return Math.Clamp((Probabilities[IndexOfMax()] - uniform) / (1 - uniform), 0, 1);
        }
    }

    /// <summary>Σ i·pᵢ over the levels, or null for a question that is not a score.</summary>
    public double? ExpectedScore => Kind == QuestionKind.Score ? Probabilities.Select((p, i) => i * p).Sum() : null;

    public double ProbabilityOf(string option)
    {
        var index = Options.IndexOf(option, StringComparer.Ordinal);
        return index < 0 ? throw new ArgumentException($"Unknown option '{option}'.", nameof(option)) : Probabilities[index];
    }

    /// <summary>The form persisted in <c>Decision</c> events.</summary>
    public ImmutableDictionary<string, double> ToProbabilityMap() =>
        Options.Zip(Probabilities).ToImmutableDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

    /// <summary>Throws when the answer does not fit the question it claims to answer.</summary>
    public DecisionAnswer Validate(DecisionQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (Kind != question.Kind || Options.IsDefault
            || !Options.SequenceEqual(question.Options.Select(option => option.Name), StringComparer.Ordinal))
            throw new ArgumentException("The answer's kind or options do not match the question.");
        if (Probabilities.IsDefault || Probabilities.Length != Options.Length
            || Probabilities.Any(p => !double.IsFinite(p) || p < 0) || Math.Abs(Probabilities.Sum() - 1) > SumTolerance)
            throw new ArgumentException("Probabilities must be finite, non-negative, one per option, and sum to 1.");
        return this;
    }

    public static DecisionAnswer For(DecisionQuestion question, IEnumerable<double> probabilities, bool calibrated = true,
        double? margin = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(probabilities);
        return new DecisionAnswer(question.Kind, [.. question.Options.Select(option => option.Name)],
            [.. probabilities], calibrated, margin).Validate(question);
    }

    /// <summary>Certain of one option: a heuristic or a parsed yes/no. Never calibrated.</summary>
    public static DecisionAnswer Certain(DecisionQuestion question, string option)
    {
        ArgumentNullException.ThrowIfNull(question);
        var index = question.Options.Select(o => o.Name).ToList().IndexOf(option);
        if (index < 0)
            throw new ArgumentException($"Unknown option '{option}'.", nameof(option));
        return For(question, question.Options.Select((_, i) => i == index ? 1.0 : 0.0), calibrated: false);
    }

    private int IndexOfMax()
    {
        var best = 0;
        for (var i = 1; i < Probabilities.Length; i++)
            if (Probabilities[i] > Probabilities[best])
                best = i;
        return best;
    }
}

public enum DeciderUnavailableReason
{
    /// <summary>The state exceeded the decider's hard stop (plan §5.1: 1,500 tokens).</summary>
    StateOverBudget,
    /// <summary>The decider declined the whole request, for example every question's row over its row budget.</summary>
    Refused,
    /// <summary>The model failed to run.</summary>
    Failed,
}

/// <summary>What a decider returns: answers, or a reason the caller must fall back to a lower tier.</summary>
public abstract record DeciderResult(string Decider);

/// <param name="Answers">By question name. A missing answer means the decider could not answer that question.</param>
public sealed record DeciderAnswers(string Decider, ImmutableDictionary<string, DecisionAnswer> Answers) : DeciderResult(Decider)
{
    /// <summary>
    /// Questions the decider declined while answering the rest, by name, with a short reason in snake case (for example
    /// <c>row_over_budget</c>). They take their below-floor action; the reason is recorded on their decision.
    /// </summary>
    public ImmutableDictionary<string, string> Declined { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed record DeciderUnavailable(string Decider, DeciderUnavailableReason Reason, string Detail) : DeciderResult(Decider);

/// <summary>
/// System One's typed-question contract and the <c>decider</c> slot's service. Shaped like Kev's API: one request is
/// one shared state plus named questions. Implementations: the degraded decider (Corvid.Fabric) and the Kev adapter in
/// Corvid.Models.Starling (C-004b).
/// </summary>
public interface IDecider
{
    /// <summary>A stable name recorded on every decision, for example <c>degraded</c> or <c>kev-0.8b</c>.</summary>
    string Name { get; }

    ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}
