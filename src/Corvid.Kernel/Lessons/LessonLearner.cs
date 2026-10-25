using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Memory;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Lessons;

/// <summary>What happened to one queued lesson opportunity.</summary>
public enum LessonLearningStatus
{
    /// <summary>Filed as a candidate for <c>/memory lessons pending</c> (always, until a calibrated decider exists).</summary>
    Filed,
    /// <summary><c>memory_op</c> let it through: approved for its project.</summary>
    Approved,
    /// <summary>Already filed (its evidence merged), or the gate called it a duplicate.</summary>
    Duplicate,
    /// <summary>The generator found no lesson, or its draft broke the quality bar or cited events outside the turn.</summary>
    Declined,
    Failed,
}

public sealed record LessonLearningResult(string OpportunityId, LessonLearningStatus Status, string? LessonId, string Detail);

public sealed record LessonLearnerOptions
{
    /// <summary>The generator's description in the decider's state.</summary>
    public string Generator { get; init; } = "unknown";

    /// <summary>Characters of each recorded event the drafting prompt shows.</summary>
    public int EventChars { get; init; } = 400;

    public int MaxOutputTokens { get; init; } = 768;
}

/// <summary>
/// Drafts lessons (C-025): <c>lesson_opportunity</c> queues a turn at egress (<see cref="Queue"/>); a background loop
/// (<see cref="RunAsync"/>, through the generator it is given, at background priority in the host) asks the generator for
/// one lesson as JSON, or none, citing the turn's event positions. Positions must lie inside the turn; the evidence's
/// content hash is SHA-256 over the cited events' persisted JSON. The draft goes through <see cref="LessonWriter"/>, which
/// validates it and gates it on <c>memory_op</c>, so without Kev every lesson waits for review. A turn that <c>/undo</c>
/// or <c>/rollback</c> took back is not drafted from.
/// </summary>
public sealed class LessonLearner : ILessonLearning
{
    /// <summary>Its fixed system prompt, Corvid's own text (A5: the egress gate neither scans nor rewrites it).</summary>
    public const string SystemPrompt =
        "You write lessons for a coding assistant's memory: something a future session in the same project should do "
        + "differently or know. Reply with one JSON object only.";

    private readonly LessonWriter _writer;
    private readonly IEventLog _log;
    private readonly IChatClient _drafter;
    private readonly LessonLearnerOptions _options;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _running = new(1, 1);

    public LessonLearner(LessonWriter writer, IEventLog log, IChatClient drafter, LessonLearnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(drafter);
        _writer = writer;
        _log = log;
        _drafter = drafter;
        _options = options ?? new LessonLearnerOptions();
    }

    /// <summary>Raised after each opportunity is processed, for a surface to mention.</summary>
    public event Action<LessonLearningResult>? Processed;

    public void Queue(LessonOpportunity opportunity)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        _writer.Store.Enqueue(opportunity.SessionId, opportunity.ProjectId, opportunity.FromSequence, opportunity.ThroughSequence,
            opportunity.DecisionId, opportunity.Request, opportunity.Tainted, opportunity.LocalOnly);
        _signal.Release();
    }

    /// <summary>Processes the queue whenever something is queued, until cancelled. Leftovers from an earlier run go first.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ProcessQueuedAsync(cancellationToken).ConfigureAwait(false);
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Processes every queued opportunity once.</summary>
    public async Task<IReadOnlyList<LessonLearningResult>> ProcessQueuedAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<LessonLearningResult>();
            foreach (var opportunity in _writer.Store.Opportunities(LessonOpportunityStates.Queued))
            {
                LessonLearningResult result;
                try
                {
                    result = await ProcessAsync(opportunity, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = new LessonLearningResult(opportunity.OpportunityId, LessonLearningStatus.Failed, null, exception.Message);
                }
                _writer.Store.SetOpportunityState(opportunity.OpportunityId, result.Status switch
                {
                    LessonLearningStatus.Filed => LessonOpportunityStates.Filed,
                    LessonLearningStatus.Approved => LessonOpportunityStates.Approved,
                    LessonLearningStatus.Duplicate => LessonOpportunityStates.Duplicate,
                    LessonLearningStatus.Declined => LessonOpportunityStates.Declined,
                    _ => LessonOpportunityStates.Failed,
                }, result.Detail, result.LessonId);
                results.Add(result);
                Processed?.Invoke(result);
            }
            return results;
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<LessonLearningResult> ProcessAsync(LessonOpportunityRow opportunity, CancellationToken cancellationToken)
    {
        LessonLearningResult Result(LessonLearningStatus status, string detail, string? lesson = null) =>
            new(opportunity.OpportunityId, status, lesson, detail);

        var turn = _log.Read(opportunity.SessionId, opportunity.FromSequence - 1, opportunity.ThroughSequence, 4_096, cancellationToken);
        if (turn.Count == 0)
            return Result(LessonLearningStatus.Failed, "the source turn is not in the log");
        // /undo or /rollback (C-014) after the turn was queued: the user took it back, so it teaches nothing.
        var session = SessionProjection.Load(_log, opportunity.SessionId, cancellationToken: cancellationToken);
        if (!session.IsVisible(opportunity.FromSequence) || !session.IsVisible(opportunity.ThroughSequence))
            return Result(LessonLearningStatus.Failed, "the source turn was undone, so no lesson is drafted from it");

        // The draft sends the source turn to the background generator, which may be a cloud binding: its gate and Vault see
        // the source session, its taint, and whether the turn's prompt held local-only memory (plan §10.3, §10.4).
        using var scope = new ModelRequestContext(opportunity.SessionId, opportunity.Tainted, opportunity.LocalOnly).Enter();
        var response = await _drafter.GetResponseAsync(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, DraftPrompt(turn))],
            new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens }, cancellationToken).ConfigureAwait(false);

        var draft = Parse(response.Text);
        if (draft is null)
            return Result(LessonLearningStatus.Declined, "the draft is not a JSON lesson");
        if (draft.None == true)
            return Result(LessonLearningStatus.Declined, "the generator found no lesson in the turn");
        var positions = (draft.Positions ?? []).Distinct().Order().ToImmutableArray();
        if (positions.IsEmpty)
            return Result(LessonLearningStatus.Declined, "the draft cites no event");
        if (positions.Any(position => position < opportunity.FromSequence || position > opportunity.ThroughSequence))
            return Result(LessonLearningStatus.Declined, string.Create(CultureInfo.InvariantCulture,
                $"the draft cites events outside the turn ({opportunity.FromSequence}–{opportunity.ThroughSequence})"));

        var cited = turn.Where(item => positions.Contains(item.Sequence)).ToList();
        var evidence = new LessonEvidence(opportunity.SessionId, positions, draft.Summary?.Trim() ?? "", ContentHash(cited));
        var content = new LessonContent(draft.Kind?.Trim() ?? "", draft.Title ?? "", draft.Applicability ?? "", draft.Body ?? "",
            [.. (draft.Tags ?? []).Select(tag => tag?.Trim() ?? "")], [evidence]);
        var filed = await _writer.FileAsync(new LessonDraft(opportunity.ProjectId, content, opportunity.LocalOnly, LessonAuthors.Agent,
            "lessons", _options.Generator), opportunity.SessionId, opportunity.Tainted, cancellationToken).ConfigureAwait(false);
        return filed.Status switch
        {
            LessonFileStatus.Invalid => Result(LessonLearningStatus.Declined,
                "the draft broke the quality bar: " + string.Join("; ", filed.Violations.Select(v => $"{v.Field} {v.Rule}"))),
            LessonFileStatus.Approved => Result(LessonLearningStatus.Approved,
                filed.Superseded is { } old ? $"approved, superseding {old}" : "approved", filed.Lesson!.LessonId),
            LessonFileStatus.Duplicate => Result(LessonLearningStatus.Duplicate, $"already filed ({filed.Lesson!.State})", filed.Lesson.LessonId),
            _ => Result(LessonLearningStatus.Filed, $"filed for review ({filed.Lesson!.ReviewReason})", filed.Lesson.LessonId),
        };
    }

    /// <summary>SHA-256 over the cited events' persisted JSON, one per line, in log order.</summary>
    internal static string ContentHash(IEnumerable<StoredEvent> cited) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", cited.Select(item => EventJson.Serialize(item.Payload))))));

    internal string DraftPrompt(IReadOnlyList<StoredEvent> turn)
    {
        var text = new StringBuilder();
        text.AppendLine("Below is one turn of a conversation, each event with its number. If it shows something a future session in "
            + "this project should do differently or know (a correction from the user, a gotcha, a debugging pattern, a convention "
            + "or a required order), write it as a lesson. Otherwise reply {\"none\": true}.");
        text.AppendLine("A lesson is one JSON object:");
        text.AppendLine("{\"kind\": \"workflow|correction|debugging_pattern|gotcha|convention\", \"title\": \"imperative, under 80 characters\", "
            + "\"applicability\": \"starts with when, before or after\", \"body\": \"3 to 10 lines: what to do; why, citing events as "
            + "'event 14' or 'events 14-19'; how to confirm it worked\", \"tags\": [\"2 to 4 lowercase tags\"], "
            + "\"positions\": [the event numbers it rests on], \"summary\": \"one line on what those events show\"}");
        text.AppendLine("Do not summarise the session. Leave out secrets, personal paths, hosts, dates, and issue or PR numbers.");
        text.AppendLine();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in turn)
        {
            var line = item.Payload switch
            {
                UserMessage user => "User: " + user.Text,
                ToolCallIssued call => "Tool call: " + (names[call.CallId] = call.ToolName) + " " + call.Arguments.GetRawText(),
                ToolResult result => (result.IsError ? "Tool error (" : "Tool result (") + names.GetValueOrDefault(result.CallId, "tool") + "): "
                    + (result.Result.ValueKind == JsonValueKind.String ? result.Result.GetString() : result.Result.GetRawText()),
                ApprovalResolved approval => approval.Approved ? null : "Approval denied: " + approval.Reason,
                AssistantMessage assistant => "Assistant: " + assistant.Text,
                _ => null,
            };
            if (line is null)
                continue;
            if (line.Length > _options.EventChars)
                line = line[.._options.EventChars] + string.Create(CultureInfo.InvariantCulture, $"… [{line.Length - _options.EventChars} more]");
            text.Append(CultureInfo.InvariantCulture, $"[event {item.Sequence}] ").AppendLine(line);
        }
        return text.ToString();
    }

    /// <summary>The JSON object inside a reply (a code fence or prose around it is ignored), or null.</summary>
    internal static LessonDraftJson? Parse(string reply)
    {
        var start = reply.IndexOf('{', StringComparison.Ordinal);
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return JsonSerializer.Deserialize(reply[start..(end + 1)], LessonDraftJsonContext.Default.LessonDraftJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The generator's reply: a lesson, or <c>{"none": true}</c>.</summary>
internal sealed record LessonDraftJson(
    string? Kind = null,
    string? Title = null,
    string? Applicability = null,
    string? Body = null,
    string?[]? Tags = null,
    long[]? Positions = null,
    string? Summary = null,
    bool? None = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(LessonDraftJson))]
internal sealed partial class LessonDraftJsonContext : JsonSerializerContext;
