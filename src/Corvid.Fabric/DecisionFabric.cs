using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric;

/// <summary>One question's result at a checkpoint.</summary>
/// <param name="Answer">The decider's answer, or null when it gave none (the below-floor action then applied).</param>
public sealed record FabricVerdict(string DecisionId, DecisionAnswer? Answer, PolicyVerdict Verdict);

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
    public FabricVerdict this[string key] => Verdicts[key];
}

/// <summary>
/// Runs one checkpoint (plan §5.2): one request to the calibrated decider, falling back to the degraded tier when there is
/// none, the state is over the hard stop, or the decider declines; then the policy; then one <c>Decision</c> event per
/// question, with its threshold, action, floor version and decider, appended as a single batch.
/// </summary>
public sealed class DecisionFabric
{
    private const int MaxAppendAttempts = 8;

    private readonly IDecider _degraded;
    private readonly IDecider? _calibrated;
    private readonly IEventLog _log;

    /// <param name="degraded">The degraded tier (§5.6); always available.</param>
    /// <param name="calibrated">The <c>decider</c> slot's calibrated decider (Kev, C-004b), or null until one exists.</param>
    public DecisionFabric(DecisionPolicy policy, IDecider degraded, IEventLog log, IDecider? calibrated = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(degraded);
        ArgumentNullException.ThrowIfNull(log);
        Policy = policy;
        _degraded = degraded;
        _log = log;
        _calibrated = calibrated;
    }

    public DecisionPolicy Policy { get; }

    /// <param name="questions">By request key: a catalogue id, or an id with an instance suffix (<c>clear_results/call-7</c>).</param>
    /// <param name="conditions">Facts some questions require before acting (<see cref="FabricConditions"/>).</param>
    public async Task<CheckpointResult> DecideAsync(string sessionId, Checkpoint checkpoint, TurnState state,
        IEnumerable<KeyValuePair<string, DecisionQuestion>> questions, IReadOnlySet<string>? conditions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(state);
        var request = new DecisionRequest(state.Json, questions);
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

        var (result, fallback) = await AskAsync(request, state, cancellationToken);
        var decider = fallback is null ? result.Decider : $"{result.Decider} after {fallback.Decider}: {WireName(fallback.Reason)}";
        var answers = result is DeciderAnswers answered ? answered.Answers : ImmutableDictionary<string, DecisionAnswer>.Empty;

        var verdicts = ImmutableDictionary.CreateBuilder<string, FabricVerdict>(StringComparer.Ordinal);
        var events = new List<EventData>();
        foreach (var (key, question) in request.Questions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var answer = answers.TryGetValue(key, out var candidate) && Fits(candidate, question) ? candidate : null;
            var verdict = Policy.Evaluate(key, answer, conditions);
            var decisionId = Guid.NewGuid().ToString("N");
            verdicts[key] = new FabricVerdict(decisionId, answer, verdict);
            events.Add(EventData.Create(new Decision(decisionId, key,
                answer?.ToProbabilityMap() ?? ImmutableDictionary<string, double>.Empty,
                answer?.Confidence ?? 0, verdict.Threshold, verdict.Action, Policy.Floors.Version, decider,
                request.State, Asked(question), answer?.Calibrated)));
        }

        var stored = Append(sessionId, events, cancellationToken);
        return new CheckpointResult(checkpoint, verdicts.ToImmutable(), decider, fallback, [.. stored]);
    }

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
