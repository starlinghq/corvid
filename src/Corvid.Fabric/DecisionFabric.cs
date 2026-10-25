using System.Collections.Immutable;
using System.Diagnostics;
using Corvid.Abstractions.Events;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric;

/// <summary>One question's result at a checkpoint.</summary>
/// <param name="Answer">The decider's answer, or null when it gave none (the below-floor action then applied).</param>
/// <param name="Decider">As recorded on this question's event: the checkpoint's decider, or the second tier's for an escalated gate.</param>
/// <param name="SkipReason">Why the question was not asked (C-030), or null when it was: the verdict is then its below-floor action.</param>
public sealed record FabricVerdict(string DecisionId, DecisionAnswer? Answer, PolicyVerdict Verdict, string? Decider = null,
    string? SkipReason = null)
{
    public bool Skipped => SkipReason is not null;
}

/// <summary>A checkpoint's results, the decider that answered, and the <c>Decision</c> events appended.</summary>
/// <param name="Decider">As recorded on the events, for example <c>degraded</c> or <c>degraded after kev-0.8b: state_over_budget</c>.</param>
/// <param name="Fallback">Why the calibrated decider was not used, when one was configured.</param>
public sealed record CheckpointResult(
    Checkpoint Checkpoint,
    ImmutableDictionary<string, FabricVerdict> Verdicts,
    string Decider,
    DeciderUnavailable? Fallback,
    ImmutableArray<StoredEvent> Events)
{
    /// <summary>The checkpoint profile it ran under (C-030): <c>full</c> or <c>reduced</c>.</summary>
    public string Profile { get; init; } = CheckpointReduction.FullProfile;

    public FabricVerdict this[string key] => Verdicts[key];
}

/// <summary>
/// Runs one checkpoint (plan §5.2): one request to the calibrated decider, falling back to the degraded tier when there is
/// none, the state is over the hard stop, or the decider declines; with a second tier configured, the gates the first tier
/// answered below their floor go to it on the same state (§5.5); then the policy; then one <c>Decision</c> event per
/// question, with its threshold, action, floor version and decider, appended as a single batch. Under a
/// <see cref="CheckpointReduction"/> (C-030), and for keys a caller skips, some questions are not asked: they take their
/// below-floor action and are logged with the decider <c>degraded skipped: &lt;reason&gt;</c>.
/// </summary>
public sealed class DecisionFabric
{
    private const int MaxAppendAttempts = 8;

    private readonly IDecider _degraded;
    private readonly IDecider? _calibrated;
    private readonly IDecider? _escalation;
    private readonly IEventLog _log;

    /// <param name="degraded">The degraded tier (§5.6); always available.</param>
    /// <param name="calibrated">The <c>decider</c> slot's calibrated decider (Kev-0.8B), or null when none is available.</param>
    /// <param name="escalation">
    /// The second tier of §5.5 (Kev-4B): asked only about gates (<c>risk</c>, <c>skill_write_safe</c>, <c>memory_op</c>) whose
    /// calibrated answer is below the floor, before the policy falls back to the user. Requires <paramref name="calibrated"/>.
    /// </param>
    /// <param name="reduction">The reduced checkpoint profile (C-030), or null for the full one.</param>
    public DecisionFabric(DecisionPolicy policy, IDecider degraded, IEventLog log, IDecider? calibrated = null, IDecider? escalation = null,
        CheckpointReduction? reduction = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(degraded);
        ArgumentNullException.ThrowIfNull(log);
        if (escalation is not null && calibrated is null)
            throw new ArgumentException("A second-tier decider needs a calibrated first tier.", nameof(escalation));
        Policy = policy;
        _degraded = degraded;
        _log = log;
        _calibrated = calibrated;
        _escalation = escalation;
        Reduction = reduction;
    }

    public DecisionPolicy Policy { get; }

    /// <summary>The calibrated first tier's name, or null when every checkpoint runs on the degraded tier.</summary>
    public string? CalibratedDecider => _calibrated?.Name;

    /// <summary>The second tier's name for gates below their floor, or null when there is none.</summary>
    public string? EscalationDecider => _escalation?.Name;

    /// <summary>The reduced checkpoint profile (C-030), or null when every checkpoint is asked whole.</summary>
    public CheckpointReduction? Reduction { get; }

    /// <summary>The profile's name in spans and surfaces: <c>full</c> or <c>reduced</c>.</summary>
    public string Profile => Reduction is null ? CheckpointReduction.FullProfile : CheckpointReduction.ReducedProfile;

    /// <summary>
    /// The decider recorded for a question that was not asked (C-030). Calibration (C-021) reads a decider named
    /// <c>degraded</c> or starting <c>degraded </c> as the degraded tier, so skipped decisions stay out of calibrated reports.
    /// </summary>
    public static string SkippedDecider(string reason) => $"{DegradedDecider.DeciderName} skipped: {reason}";

    /// <param name="questions">By request key: a catalogue id, or an id with an instance suffix (<c>clear_results/call-7</c>).</param>
    /// <param name="conditions">
    /// Facts some questions require before acting (<see cref="FabricConditions"/>). A condition some catalogue question grants
    /// (<see cref="CatalogueQuestion.Grants"/>) is ignored here: only its granter acting at this checkpoint makes it hold.
    /// </param>
    public Task<CheckpointResult> DecideAsync(string sessionId, Checkpoint checkpoint, TurnState state,
        IEnumerable<KeyValuePair<string, DecisionQuestion>> questions, IReadOnlySet<string>? conditions = null,
        CancellationToken cancellationToken = default) =>
        DecideAsync(sessionId, checkpoint, state, questions, conditions, skip: null, cancellationToken);

    /// <param name="skip">
    /// Keys of the request not to ask, with why (C-030): only for a question whose answer the caller can show does not
    /// change the outcome. Each takes its below-floor action and is logged as skipped. Honoured at any checkpoint and for
    /// any question, gates included, so the caller carries the proof.
    /// </param>
    public async Task<CheckpointResult> DecideAsync(string sessionId, Checkpoint checkpoint, TurnState state,
        IEnumerable<KeyValuePair<string, DecisionQuestion>> questions, IReadOnlySet<string>? conditions,
        IReadOnlyDictionary<string, string>? skip, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(state);
        var request = new DecisionRequest(state.Json, questions);
        using var activity = FabricTelemetry.Source.StartActivity(ActivityKind.Internal, tags:
        [
            new("gen_ai.conversation.id", sessionId),
            new("corvid.checkpoint", CheckpointWireName(checkpoint)),
            new("corvid.checkpoint.profile", Profile),
        ], name: FabricTelemetry.DecideSpan);
        foreach (var (key, question) in request.Questions)
        {
            var entry = Policy.Catalogue.Resolve(key);
            if (!entry.Checkpoints.Contains(checkpoint))
                throw new ArgumentException($"'{entry.Id}' is not asked at the {checkpoint} checkpoint.", nameof(questions));
            if (entry.Kind != question.Kind)
                throw new ArgumentException($"'{key}' must be a {entry.Kind} question.", nameof(questions));
            // Policy reads score levels by index (risk 3–4 ask) and floors assume the catalogue's options, so a reordered
            // or shortened list could turn "destructive" into level 0.
            if (entry.HasFixedOptions && !question.Options.Select(option => option.Name)
                    .SequenceEqual(entry.Options.Select(option => option.Name), StringComparer.Ordinal))
                throw new ArgumentException($"'{key}' must use the catalogue's options in order.", nameof(questions));
        }

        // C-030: skipped keys reach no decider, and when nothing is left to ask no decider is called at all.
        var (asked, skipped) = CheckpointReduction.Partition(Policy.Catalogue, checkpoint, request.Questions.Keys, Reduction, skip);
        var answers = ImmutableDictionary<string, DecisionAnswer>.Empty;
        var deciders = ImmutableDictionary<string, string>.Empty;
        var decider = asked.IsEmpty ? SkippedDecider(skipped.Values.First()) : DegradedDecider.DeciderName;
        DeciderUnavailable? fallback = null;
        if (!asked.IsEmpty)
        {
            var askedRequest = skipped.IsEmpty ? request
                : new DecisionRequest(request.State, request.Questions.Where(pair => !skipped.ContainsKey(pair.Key)));
            DeciderResult result;
            (result, fallback) = await AskAsync(askedRequest, state, cancellationToken);
            decider = fallback is null ? result.Decider : $"{result.Decider} after {fallback.Decider}: {WireName(fallback.Reason)}";
            if (result is DeciderAnswers answered)
                answers = answered.Answers;
            if (result is DeciderAnswers { Declined.IsEmpty: false } partial)
                deciders = partial.Declined.ToImmutableDictionary(pair => pair.Key, pair => $"{decider} declined: {pair.Value}", StringComparer.Ordinal);
            if (fallback is null && _escalation is not null && result is DeciderAnswers first)
            {
                var (escalatedAnswers, escalatedBy) = await EscalateAsync(askedRequest, first, cancellationToken);
                answers = escalatedAnswers;
                deciders = deciders.SetItems(escalatedBy);
            }
        }
        // A skipped key has no answer, so the policy gives it its below-floor action.
        answers = answers.RemoveRange(skipped.Keys);
        deciders = deciders.SetItems(skipped.Select(pair => KeyValuePair.Create(pair.Key, SkippedDecider(pair.Value))));

        // Granting questions are evaluated first (C-026): a granted condition holds for the rest of the checkpoint only when its
        // granter acted, whatever the caller passed. Events are still written in key order.
        var ordered = request.Questions.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var held = new HashSet<string>(conditions ?? ImmutableHashSet<string>.Empty, StringComparer.Ordinal);
        held.ExceptWith(Policy.Catalogue.Granted);
        var evaluated = new Dictionary<string, (DecisionAnswer? Answer, PolicyVerdict Verdict)>(StringComparer.Ordinal);
        foreach (var grants in new[] { true, false })
            foreach (var (key, question) in ordered.Where(pair => Policy.Catalogue.Resolve(pair.Key).Grants is not null == grants))
            {
                var answer = answers.TryGetValue(key, out var candidate) && Fits(candidate, question) ? candidate : null;
                var verdict = Policy.Evaluate(key, answer, held);
                if (skipped.TryGetValue(key, out var why))
                    verdict = verdict with { Reason = "skipped: " + why };
                evaluated[key] = (answer, verdict);
                if (grants && verdict.Acted)
                    held.Add(Policy.Catalogue.Resolve(key).Grants!);
            }

        var verdicts = ImmutableDictionary.CreateBuilder<string, FabricVerdict>(StringComparer.Ordinal);
        var events = new List<EventData>();
        foreach (var (key, question) in ordered)
        {
            var (answer, verdict) = evaluated[key];
            var decisionId = Guid.NewGuid().ToString("N");
            var answeredBy = deciders.GetValueOrDefault(key, decider);
            verdicts[key] = new FabricVerdict(decisionId, answer, verdict, answeredBy, skipped.GetValueOrDefault(key));
            events.Add(EventData.Create(new Decision(decisionId, key,
                answer?.ToProbabilityMap() ?? ImmutableDictionary<string, double>.Empty,
                answer?.Confidence ?? 0, verdict.Threshold, verdict.Action, Policy.Floors.Version, answeredBy,
                request.State, Asked(question), answer?.Calibrated)));
        }

        var stored = Append(sessionId, events, cancellationToken);
        var checkpointResult = new CheckpointResult(checkpoint, verdicts.ToImmutable(), decider, fallback, [.. stored]) { Profile = Profile };
        if (activity is not null)
            Describe(activity, checkpointResult);
        return checkpointResult;
    }

    /// <summary>
    /// A fabric with the same policy, degraded tier, log and checkpoint profile, but other calibrated deciders (A5: a
    /// session's own decider, from its mix or a person's switch). Decisions it records go to the same log, named by their
    /// decider as ever, so calibration reads them alike. The checkpoint profile (C-030's reduction) is this fabric's: it was
    /// chosen for the configured decider's placement and is not worked out again.
    /// </summary>
    /// <param name="calibrated">The first tier, or null for the degraded tier alone.</param>
    /// <param name="escalation">The second tier for gates below their floor; needs <paramref name="calibrated"/>.</param>
    public DecisionFabric WithDeciders(IDecider? calibrated, IDecider? escalation = null) =>
        ReferenceEquals(calibrated, _calibrated) && ReferenceEquals(escalation, _escalation)
            ? this
            : new DecisionFabric(Policy, _degraded, _log, calibrated, calibrated is null ? null : escalation, Reduction);

    /// <summary>Appends a later outcome for a decision (plan §5.2): the tool succeeded, the user approved, corrected or retried.</summary>
    public StoredEvent RecordOutcome(string sessionId, string decisionId, string outcome, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        return Append(sessionId, [EventData.Create(new DecisionOutcomeRecorded(decisionId, outcome))], cancellationToken)[0];
    }

    private async Task<(DeciderResult Result, DeciderUnavailable? Fallback)> AskAsync(DecisionRequest request, TurnState state,
        CancellationToken cancellationToken)
    {
        DeciderUnavailable? fallback = null;
        if (_calibrated is not null)
        {
            if (state.ExceedsHardStop)
            {
                fallback = new DeciderUnavailable(_calibrated.Name, DeciderUnavailableReason.StateOverBudget,
                    $"turn state is {state.Tokens} tokens");
            }
            else
            {
                DeciderResult result;
                try
                {
                    result = await _calibrated.DecideAsync(request, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = new DeciderUnavailable(_calibrated.Name, DeciderUnavailableReason.Failed, exception.Message);
                }
                if (result is not DeciderUnavailable unavailable)
                    return (result, null);
                fallback = unavailable;
            }
        }
        return (await _degraded.DecideAsync(request, cancellationToken), fallback);
    }

    /// <summary>
    /// The two-tier decider (§5.5): the gates the first tier answered, calibrated, below their floor are asked again of the
    /// second tier on the same state, in one request. Its answers replace the first tier's and are recorded as
    /// <c>kev-4b after kev-0.8b: below_floor</c>; when it declines or fails, the first tier's answer stands, recorded as
    /// <c>kev-0.8b after kev-4b: failed</c>, and the policy takes the below-floor action as before. Floors are compared
    /// without the caller's conditions: a missing condition holds either answer back anyway.
    /// </summary>
    private async Task<(ImmutableDictionary<string, DecisionAnswer> Answers, ImmutableDictionary<string, string> Deciders)> EscalateAsync(
        DecisionRequest request, DeciderAnswers first, CancellationToken cancellationToken)
    {
        var below = request.Questions
            .Where(pair => Policy.Catalogue.Resolve(pair.Key).Role == QuestionRole.Gate
                && first.Answers.TryGetValue(pair.Key, out var answer) && answer.Calibrated && Fits(answer, pair.Value)
                && !Policy.ClearsFloor(pair.Key, answer))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        if (below.Length == 0)
            return (first.Answers, ImmutableDictionary<string, string>.Empty);

        DeciderResult second;
        try
        {
            second = await _escalation!.DecideAsync(new DecisionRequest(request.State, below), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            second = new DeciderUnavailable(_escalation!.Name, DeciderUnavailableReason.Failed, exception.Message);
        }

        var answers = first.Answers.ToBuilder();
        var deciders = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, question) in below)
        {
            if (second is DeciderAnswers escalated && escalated.Answers.TryGetValue(key, out var answer) && Fits(answer, question))
            {
                answers[key] = answer;
                deciders[key] = $"{escalated.Decider} after {first.Decider}: below_floor";
            }
            else
            {
                var reason = second is DeciderUnavailable unavailable ? WireName(unavailable.Reason) : "no_answer";
                deciders[key] = $"{first.Decider} after {second.Decider}: {reason}";
            }
        }
        return (answers.ToImmutable(), deciders.ToImmutable());
    }

    /// <summary>The persisted form of a question as asked, for calibration and Kev training export (C-021).</summary>
    public static AskedQuestion Asked(DecisionQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var instructions = question.Instructions.Json ?? JsonSerializer.SerializeToElement(question.Instructions.Text!,
            FabricJsonContext.Default.String);
        return new AskedQuestion(KindWireName(question.Kind), instructions,
            [.. question.Options.Select(option => new AskedOption(option.Name, option.Description))]);
    }

    /// <summary>The persisted name of a question kind. Renaming one changes the meaning of recorded decisions.</summary>
    public static string KindWireName(QuestionKind kind) => kind switch
    {
        QuestionKind.Noul => "noul",
        QuestionKind.Choice => "choice",
        QuestionKind.Score => "score",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The checkpoint's name as spans and surfaces show it.</summary>
    public static string CheckpointWireName(Checkpoint checkpoint) => checkpoint switch
    {
        Checkpoint.Ingress => "ingress",
        Checkpoint.Step => "step",
        Checkpoint.PreExecute => "pre_execute",
        Checkpoint.Egress => "egress",
        Checkpoint.Tick => "tick",
        _ => throw new ArgumentOutOfRangeException(nameof(checkpoint)),
    };

    // Arrays in key order, so a trace viewer lines a question up with its confidence and action; and one event per
    // question for viewers that show events rather than array attributes.
    private static void Describe(Activity activity, CheckpointResult result)
    {
        var verdicts = result.Verdicts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        activity.SetTag("corvid.decider", result.Decider);
        if (result.Fallback is { } fallback)
            activity.SetTag("corvid.decider.fallback", WireName(fallback.Reason));
        var escalated = verdicts.Where(pair => !pair.Value.Skipped && pair.Value.Decider != result.Decider).Select(pair => pair.Key).ToArray();
        if (escalated.Length > 0)
            activity.SetTag("corvid.decider.escalated", escalated);
        activity.SetTag("corvid.decide.questions", verdicts.Select(pair => pair.Key).ToArray());
        activity.SetTag("corvid.decide.confidences", verdicts.Select(pair => pair.Value.Answer?.Confidence ?? 0).ToArray());
        activity.SetTag("corvid.decide.actions", verdicts.Select(pair => pair.Value.Verdict.Action).ToArray());
        activity.SetTag("corvid.decide.acted", verdicts.Select(pair => pair.Value.Verdict.Acted).ToArray());
        // C-030: the questions not asked and why, in key order (empty arrays when every question was asked).
        var skipped = verdicts.Where(pair => pair.Value.Skipped).ToArray();
        activity.SetTag("corvid.decide.skipped", skipped.Select(pair => pair.Key).ToArray());
        activity.SetTag("corvid.decide.skip_reasons", skipped.Select(pair => pair.Value.SkipReason!).ToArray());
        foreach (var (key, verdict) in verdicts)
            activity.AddEvent(new ActivityEvent("corvid.decision", tags: new ActivityTagsCollection
            {
                ["corvid.question.id"] = key,
                ["corvid.decision.id"] = verdict.DecisionId,
                ["corvid.decider"] = verdict.Decider,
                ["corvid.confidence"] = verdict.Answer?.Confidence ?? 0,
                ["corvid.threshold"] = verdict.Verdict.Threshold,
                ["corvid.action"] = verdict.Verdict.Action,
                ["corvid.acted"] = verdict.Verdict.Acted,
                ["corvid.answered"] = verdict.Answer is not null,
                ["corvid.skipped"] = verdict.Skipped,
            }));
    }

    // A malformed answer is not trusted: the question takes its below-floor action instead.
    private static bool Fits(DecisionAnswer answer, DecisionQuestion question)
    {
        try
        {
            answer.Validate(question);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // Decision events do not depend on the head, so a concurrent writer only means retrying at the new head.
    private IReadOnlyList<StoredEvent> Append(string sessionId, IReadOnlyList<EventData> events, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var head = _log.GetHead(sessionId, cancellationToken);
            try
            {
                return _log.Append(sessionId, head, events, cancellationToken);
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }

    private static string WireName(DeciderUnavailableReason reason) => reason switch
    {
        DeciderUnavailableReason.StateOverBudget => "state_over_budget",
        DeciderUnavailableReason.Refused => "refused",
        DeciderUnavailableReason.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

[JsonSerializable(typeof(string))]
internal sealed partial class FabricJsonContext : JsonSerializerContext;
