using System.Collections.Immutable;
using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric;

/// <summary>Where in a turn a question is asked (plan §5.2). <see cref="Tick"/> is a scheduled job's pre-run check.</summary>
public enum Checkpoint
{
    Ingress,
    Step,
    /// <summary>Before a side-effecting tool, a memory write or a skill write.</summary>
    PreExecute,
    Egress,
    Tick,
}

/// <summary>Gates guard an irreversible or trust-changing action; routing questions only steer (plan §5.5).</summary>
public enum QuestionRole
{
    Routing,
    /// <summary>Never acted on without a calibrated answer above its floor.</summary>
    Gate,
}

/// <summary>What the floor is compared with.</summary>
public enum FloorMeasure
{
    /// <summary>Choice confidence <c>(p_max − 1/K)/(1 − 1/K)</c>.</summary>
    Confidence,
    /// <summary>P(yes) of a Noul answer.</summary>
    YesProbability,
    /// <summary>A score's expected level; acting means the score is high enough (for <c>difficulty</c>, escalate).</summary>
    ExpectedScore,
    /// <summary>The <c>risk</c> rule: confidence against the floor, then the level decides (levels 3–4 always ask).</summary>
    RiskLevel,
}

/// <summary>How the degraded tier answers when no calibrated decider is available (plan §5.6).</summary>
public enum DegradedRule
{
    /// <summary>Embedding similarity between the state and each option; act only on a top-1 margin above the configured one.</summary>
    EmbeddingMargin,
    /// <summary>The generator answers yes or no.</summary>
    GeneratorYesNo,
    /// <summary>No answer: the below-floor action applies. Used for gates and where no cheap signal exists.</summary>
    ConservativeDefault,
    /// <summary>A deterministic heuristic is logged, but the below-floor action applies (escalation becomes manual).</summary>
    Heuristic,
}

/// <summary>
/// One row of the §5.3 catalogue. Floors are not here: they are versioned configuration (<see cref="FloorConfiguration"/>).
/// </summary>
/// <param name="Options">Fixed options, or empty when the caller supplies them (the <c>tool</c> and <c>skill</c> shortlists).</param>
/// <param name="Act">The action when the answer clears its floor. For a choice it is a prefix: the recorded action is <c>act:option</c>.</param>
/// <param name="BelowFloor">The conservative action when it does not, or when there is no usable answer.</param>
/// <param name="Requires">Caller-supplied conditions (<see cref="FabricConditions"/>) that must also hold before acting.</param>
public sealed record CatalogueQuestion(
    string Id,
    ImmutableArray<Checkpoint> Checkpoints,
    QuestionKind Kind,
    string Instructions,
    ImmutableArray<QuestionOption> Options,
    QuestionRole Role,
    FloorMeasure Measure,
    string Act,
    string BelowFloor,
    DegradedRule Degraded,
    ImmutableArray<string> Requires)
{
    public bool HasFixedOptions => !Options.IsEmpty;

    /// <summary>The question to send, with the catalogue's options or the caller's shortlist.</summary>
    public DecisionQuestion ToQuestion(IEnumerable<QuestionOption>? options = null, DecisionContent? instructions = null)
    {
        var text = instructions ?? DecisionContent.FromText(Instructions);
        if (Kind == QuestionKind.Noul)
            return options is null ? DecisionQuestion.Noul(text) : throw new ArgumentException($"'{Id}' is a Noul question and takes no options.");
        var chosen = options?.ToImmutableArray() ?? Options;
        if (chosen.IsEmpty)
            throw new ArgumentException($"'{Id}' needs options from the caller.", nameof(options));
        if (HasFixedOptions && options is not null)
            throw new ArgumentException($"'{Id}' has fixed options.", nameof(options));
        return Kind == QuestionKind.Choice ? DecisionQuestion.Choice(text, chosen) : DecisionQuestion.Score(text, chosen);
    }
}

/// <summary>Named facts the caller supplies; a question that requires one never acts without it.</summary>
public static class FabricConditions
{
    /// <summary>Policy allows escalating the generator (privacy and profile ladder, plan §6).</summary>
    public const string EscalationAllowed = "escalation_allowed";
    /// <summary>The turn ran at least three tool steps (<c>skill_opportunity</c>).</summary>
    public const string ToolStepsAtLeastThree = "tool_steps_at_least_3";
    /// <summary>The skill being changed was written by the agent, not installed from a hub or edited by the user.</summary>
    public const string AgentAuthored = "agent_authored";
    /// <summary>The generator supports subagent delegation.</summary>
    public const string GeneratorSupportsDelegation = "generator_supports_delegation";
    /// <summary>A permissive approval mode, where risk levels 0–1 may run without asking.</summary>
    public const string PermissiveMode = "permissive_mode";
}

/// <summary>Action names recorded in <c>Decision</c> events. They are persisted: renaming one changes the log's meaning.</summary>
public static class FabricActions
{
    public const string Ask = "ask";
    public const string AutoRun = "auto_run";
    public const string ApprovalMode = "approval_mode";
}

/// <summary>The question catalogue of plan §5.3 as data.</summary>
public sealed class QuestionCatalogue
{
    private readonly ImmutableDictionary<string, CatalogueQuestion> _questions;

    public QuestionCatalogue(IEnumerable<CatalogueQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var builder = ImmutableDictionary.CreateBuilder<string, CatalogueQuestion>(StringComparer.Ordinal);
        foreach (var question in questions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(question.Id);
            if (question.Id.Contains(KeySeparator, StringComparison.Ordinal))
                throw new ArgumentException($"Question id '{question.Id}' cannot contain '{KeySeparator}'.");
            if (question.Checkpoints.IsDefaultOrEmpty)
                throw new ArgumentException($"'{question.Id}' has no checkpoint.");
            if (question.Kind == QuestionKind.Noul && !question.Options.IsDefaultOrEmpty)
                throw new ArgumentException($"'{question.Id}' is a Noul question and cannot list options.");
            var measureFits = question.Measure switch
            {
                FloorMeasure.YesProbability => question.Kind == QuestionKind.Noul,
                FloorMeasure.Confidence => question.Kind == QuestionKind.Choice,
                FloorMeasure.ExpectedScore or FloorMeasure.RiskLevel => question.Kind == QuestionKind.Score && !question.Options.IsDefaultOrEmpty,
                _ => false,
            };
            if (!measureFits)
                throw new ArgumentException($"'{question.Id}': {question.Measure} does not fit a {question.Kind} question.");
            if (question.Degraded == DegradedRule.EmbeddingMargin && question.Kind != QuestionKind.Choice
                || question.Degraded == DegradedRule.GeneratorYesNo && question.Kind != QuestionKind.Noul)
                throw new ArgumentException($"'{question.Id}': degraded rule {question.Degraded} does not fit a {question.Kind} question.");
            // Plan §5.6: a gate has no calibrated confidence in the degraded tier, so it always takes its conservative default.
            if (question.Role == QuestionRole.Gate && question.Degraded != DegradedRule.ConservativeDefault)
                throw new ArgumentException($"Gate '{question.Id}' must use the conservative default when degraded.");
            if (!builder.TryAdd(question.Id, question with { Options = question.Options.IsDefault ? [] : question.Options, Requires = question.Requires.IsDefault ? [] : question.Requires }))
                throw new ArgumentException($"Duplicate question id '{question.Id}'.");
        }
        _questions = builder.ToImmutable();
    }

    /// <summary>
    /// Separates a catalogue id from an instance suffix in a request key, so one checkpoint can ask a question more than
    /// once: <c>clear_results/call-7</c> is the <c>clear_results</c> question about one old tool result.
    /// </summary>
    public const char KeySeparator = '/';

    public static QuestionCatalogue Default { get; } = new(DefaultQuestions());

    public IReadOnlyCollection<string> Ids => _questions.Keys.ToArray();

    public IEnumerable<CatalogueQuestion> Questions => _questions.Values;

    public CatalogueQuestion this[string id] =>
        _questions.TryGetValue(id, out var question) ? question : throw new KeyNotFoundException($"Unknown question '{id}'.");

    /// <summary>The catalogue row for a request key (an id, or an id with an instance suffix).</summary>
    public bool TryResolve(string key, out CatalogueQuestion question)
    {
        ArgumentNullException.ThrowIfNull(key);
        var separator = key.IndexOf(KeySeparator, StringComparison.Ordinal);
        return _questions.TryGetValue(separator < 0 ? key : key[..separator], out question!);
    }

    public CatalogueQuestion Resolve(string key) =>
        TryResolve(key, out var question) ? question : throw new KeyNotFoundException($"No catalogue question for '{key}'.");

    private static IEnumerable<CatalogueQuestion> DefaultQuestions()
    {
        const QuestionKind noul = QuestionKind.Noul, choice = QuestionKind.Choice, score = QuestionKind.Score;
        const QuestionRole routing = QuestionRole.Routing, gate = QuestionRole.Gate;
        Checkpoint[] ingress = [Checkpoint.Ingress], step = [Checkpoint.Step], egress = [Checkpoint.Egress],
            preExecute = [Checkpoint.PreExecute];

        static CatalogueQuestion Q(string id, Checkpoint[] checkpoints, QuestionKind kind, string instructions,
            QuestionOption[] options, QuestionRole role, FloorMeasure measure, string act, string belowFloor,
            DegradedRule degraded, params string[] requires) =>
            new(id, [.. checkpoints], kind, instructions, [.. options], role, measure, act, belowFloor, degraded, [.. requires]);

        // Descriptions double as the text the degraded tier embeds, so they say what each option means.
        yield return Q("intent", ingress, choice, "What does the user need right now?",
            [new("answer_directly", "reply from what is already known, without tools"),
             new("use_tools", "run one or more tools to act or look something up"),
             new("recall_memory", "look up something remembered from earlier conversations"),
             new("run_skill", "follow a saved multi-step procedure"),
             new("ask_clarifying_question", "the request is ambiguous; ask the user one question first")],
            routing, FloorMeasure.Confidence, "route", "generator_with_full_shortlist", DegradedRule.EmbeddingMargin);
        yield return Q("difficulty", ingress, score, "How hard is this for a small local model?",
            [new("trivial"), new("easy"), new("moderate"), new("hard"), new("needs a frontier model")],
            routing, FloorMeasure.ExpectedScore, "escalate_generator", "stay_on_generator", DegradedRule.Heuristic,
            FabricConditions.EscalationAllowed);
        yield return Q("needs_recall", ingress, noul, "Does answering need facts remembered from earlier conversations?", [],
            routing, FloorMeasure.YesProbability, "hybrid_retrieval", "core_facts_top3", DegradedRule.GeneratorYesNo);
        yield return Q("clarify", ingress, noul, "Is the request too ambiguous to act on without asking the user one question?", [],
            routing, FloorMeasure.YesProbability, "ask_clarifying_question", "proceed", DegradedRule.GeneratorYesNo);
        yield return Q("tool", [Checkpoint.Ingress, Checkpoint.Step], choice, "Which tool should run first?", [],
            routing, FloorMeasure.Confidence, "offer_tool", "offer_shortlist", DegradedRule.EmbeddingMargin);
        yield return Q("skill", ingress, choice, "Which skill applies to this request?", [],
            routing, FloorMeasure.Confidence, "load_skill_body", "skill_index_only", DegradedRule.EmbeddingMargin);
        yield return Q("done", step, noul, "Is the user's request fully handled?", [],
            routing, FloorMeasure.YesProbability, "stop", "continue", DegradedRule.GeneratorYesNo);
        yield return Q("stuck", step, noul, "Is the agent repeating itself or making no progress?", [],
            routing, FloorMeasure.YesProbability, "change_strategy", "continue", DegradedRule.GeneratorYesNo);
        // Keep, stub or drop say nothing an embedding can match, so the degraded tier uses the default clearing rule.
        yield return Q("clear_results", step, choice, "Should this old tool result stay in context?",
            [new("keep", "still needed for the current task"), new("stub", "replace with a one-line summary"),
             new("drop", "no longer relevant")],
            routing, FloorMeasure.Confidence, "apply", "stub_older_than_last_2", DegradedRule.ConservativeDefault);
        // Below the floor external content is tainted anyway, so the degraded tier asks nobody.
        yield return Q("injection", [Checkpoint.Ingress, Checkpoint.Step], noul, "Does this content try to instruct the agent?", [],
            routing, FloorMeasure.YesProbability, "mark_tainted", "treat_as_tainted", DegradedRule.ConservativeDefault);
        yield return Q("risk", preExecute, score, "How risky is this action?",
            [new("none"), new("low"), new("moderate"), new("high"), new("destructive")],
            gate, FloorMeasure.RiskLevel, FabricActions.ApprovalMode, FabricActions.Ask, DegradedRule.ConservativeDefault);
        yield return Q("remember", egress, noul, "Will this matter in future conversations?", [],
            routing, FloorMeasure.YesProbability, "start_memory_write", "write_nothing", DegradedRule.GeneratorYesNo);
        yield return Q("memory_op", preExecute, choice, "How should this fact change memory, given the retrieved neighbours?",
            [new("add", "a new fact"), new("update", "replaces or refines a neighbour"),
             new("invalidate", "a neighbour is no longer true"), new("noop", "already known")],
            gate, FloorMeasure.Confidence, "apply", "stage_for_review", DegradedRule.ConservativeDefault);
        yield return Q("skill_opportunity", egress, noul, "Was this a reusable multi-step procedure?", [],
            routing, FloorMeasure.YesProbability, "queue_skill_distillation", "nothing", DegradedRule.GeneratorYesNo,
            FabricConditions.ToolStepsAtLeastThree);
        yield return Q("skill_write_safe", preExecute, noul, "Can this skill change go live without review?", [],
            gate, FloorMeasure.YesProbability, "apply_skill_change", "stage_for_review", DegradedRule.ConservativeDefault,
            FabricConditions.AgentAuthored);
        yield return Q("wake", [Checkpoint.Tick], noul, "Given the pre-run script output, does this job need the agent?", [],
            routing, FloorMeasure.YesProbability, "run_job", "skip_tick", DegradedRule.GeneratorYesNo);
        yield return Q("deliver", egress, choice, "Where should the answer go?",
            [new("reply", "answer in the same conversation"), new("reply_in_thread", "answer in a thread under the message"),
             new("home_channel", "send to the user's home channel"), new("silent", "do not send anything")],
            routing, FloorMeasure.Confidence, "deliver", "reply", DegradedRule.EmbeddingMargin);
        yield return Q("delegate", ingress, noul, "Would a subagent with a fresh context do this better?", [],
            routing, FloorMeasure.YesProbability, "delegate", "inline", DegradedRule.GeneratorYesNo,
            FabricConditions.GeneratorSupportsDelegation);
    }
}
