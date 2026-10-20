using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Turns;
using Microsoft.Extensions.AI;

namespace Corvid.Skills;

/// <summary>What happened to one queued opportunity.</summary>
public enum LearningStatus
{
    /// <summary><c>skill_write_safe</c> let the skill go live: it was committed as the agent's.</summary>
    Applied,
    /// <summary>Waiting in <c>/skills pending</c> (always, until a calibrated decider exists).</summary>
    Staged,
    Failed,
}

public sealed record LearningResult(string OpportunityId, LearningStatus Status, string? Skill, string Detail, PendingSkill? Pending = null);

public sealed record SkillLearnerOptions
{
    /// <summary>The generator's description in the decider's state, as the turn loop writes it.</summary>
    public string Generator { get; init; } = "unknown";

    /// <summary>Characters of each recorded tool result the drafting prompt shows.</summary>
    public int ResultChars { get; init; } = 400;

    public int MaxOutputTokens { get; init; } = 1_024;
}

/// <summary>
/// The learning loop (plan §11): <c>skill_opportunity</c> queues a turn (<see cref="Queue"/>); a background job
/// (<see cref="RunAsync"/>, on the device scheduler through the generator it is given) drafts a skill from the turn's
/// events with the generator; the draft must be well formed, pass the scanner and pass its replay test
/// (<see cref="SkillReplayer"/>); then <c>skill_write_safe</c> gates it at the pre-execute checkpoint. Only a calibrated
/// answer above the floor, on a skill the agent wrote (the <c>agent_authored</c> condition), applies it; otherwise the
/// draft is staged for <c>/skills pending</c>. The degraded decider never answers a gate, so without Kev every draft is
/// staged, and a draft never touches a skill the user or a hub wrote, approved or not.
/// </summary>
public sealed class SkillLearner : ISkillLearning
{
    private const string SystemPrompt =
        "You write reusable skills for an AI assistant in the agentskills.io SKILL.md format. Reply with the SKILL.md only.";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly SkillStore _store;
    private readonly SkillsDatabase _database;
    private readonly IEventLog _log;
    private readonly DecisionFabric _fabric;
    private readonly SkillReplayer _replayer;
    private readonly IChatClient _drafter;
    private readonly SkillCurator? _curator;
    private readonly SkillLearnerOptions _options;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <param name="drafter">The generator that writes drafts; the host passes it at background priority.</param>
    /// <param name="curator">Runs the skill's evals after it changes; optional.</param>
    public SkillLearner(SkillStore store, SkillsDatabase database, IEventLog log, DecisionFabric fabric, SkillReplayer replayer,
        IChatClient drafter, SkillCurator? curator = null, SkillLearnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(replayer);
        ArgumentNullException.ThrowIfNull(drafter);
        _store = store;
        _database = database;
        _log = log;
        _fabric = fabric;
        _replayer = replayer;
        _drafter = drafter;
        _curator = curator;
        _options = options ?? new SkillLearnerOptions();
    }

    /// <summary>Raised after each opportunity is processed, for a surface to mention.</summary>
    public event Action<LearningResult>? Processed;

    public void Queue(SkillOpportunity opportunity)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        _database.Enqueue(opportunity.SessionId, opportunity.FromSequence, opportunity.ThroughSequence, opportunity.DecisionId, opportunity.Request,
            opportunity.Tainted);
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
    public async Task<IReadOnlyList<LearningResult>> ProcessQueuedAsync(CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<LearningResult>();
            foreach (var opportunity in _database.Opportunities(OpportunityState.Queued))
            {
                LearningResult result;
                try
                {
                    result = await ProcessAsync(opportunity, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    result = new LearningResult(opportunity.Id, LearningStatus.Failed, null, exception.Message);
                }
                _database.SetState(opportunity.Id, result.Status switch
                {
                    LearningStatus.Applied => OpportunityState.Applied,
                    LearningStatus.Staged => OpportunityState.Staged,
                    _ => OpportunityState.Failed,
                }, result.Detail);
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

    private async Task<LearningResult> ProcessAsync(OpportunityRow opportunity, CancellationToken cancellationToken)
    {
        LearningResult Fail(string detail, string? skill = null) => new(opportunity.Id, LearningStatus.Failed, skill, detail);

        var turn = _log.Read(opportunity.SessionId, opportunity.FromSequence - 1, opportunity.ThroughSequence, 4_096, cancellationToken);
        if (turn.Count == 0)
            return Fail("the source turn is not in the log");

        // 2. Draft (System Two work).
        var response = await _drafter.GetResponseAsync(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, DraftPrompt(turn))],
            new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens }, cancellationToken).ConfigureAwait(false);
        SkillDocument draft;
        try
        {
            draft = SkillDocument.Parse(ExtractSkill(response.Text));
        }
        catch (SkillFormatException exception)
        {
            return Fail("the draft is not a valid SKILL.md: " + exception.Message);
        }
        if (SkillScanner.Scan(SkillDocument.FileName, draft.Text) is { IsEmpty: false } findings)
            return Fail("the scanner flagged the draft: " + string.Join("; ", findings.Select(finding => $"line {finding.Line} {finding.Rule}")), draft.Name);
        if (_store.TryGet(draft.Name, out var existing) && !existing.AgentOwned)
            return Fail($"a skill named {draft.Name} exists and is not agent-authored, so the learning loop leaves it alone", draft.Name);

        // 3. The draft must pass its own test.
        var replay = await _replayer.ReplayAsync(_log, opportunity.SessionId, opportunity.FromSequence, opportunity.ThroughSequence, draft,
            cancellationToken).ConfigureAwait(false);
        if (!replay.Passed)
            return Fail("the replay test " + replay.Describe(), draft.Name);

        // 4. skill_write_safe gates applying it. The draft is new or replaces an agent-authored skill, so agent_authored holds;
        // the store checks it again under the repository's lock before writing.
        var verdict = await AskWriteSafeAsync(opportunity.SessionId, opportunity.Request, draft, existing is null ? "create" : "update", replay,
            cancellationToken).ConfigureAwait(false);
        // A turn that held outside content (§10.3) would pass it into a trusted, agent-authored skill: whatever the gate says,
        // the user reviews such a draft.
        if (verdict.Verdict.Acted && !opportunity.Tainted)
        {
            var revision = await _store.WriteAgentSkillAsync(draft, opportunity.SessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RunEvalsAsync(draft.Name, cancellationToken).ConfigureAwait(false);
            return new LearningResult(opportunity.Id, LearningStatus.Applied, draft.Name, $"applied as {revision?[..12] ?? "unchanged"}; replay {replay.Describe()}");
        }
        var reason = opportunity.Tainted ? "the source turn held outside content, so the draft waits for review" : verdict.Verdict.Reason;
        var pending = _database.Stage(draft.Name, draft.Text, opportunity.SessionId, verdict.DecisionId, reason, replay.Describe(), opportunity.Id);
        return new LearningResult(opportunity.Id, LearningStatus.Staged, draft.Name, $"staged as {pending.Id} ({reason})", pending);
    }

    private async Task<FabricVerdict> AskWriteSafeAsync(string sessionId, string request, SkillDocument draft, string change, ReplayOutcome replay,
        CancellationToken cancellationToken)
    {
        var question = _fabric.Policy.Catalogue["skill_write_safe"];
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("question", question.Instructions);
            writer.WriteString("skill", draft.Name);
            writer.WriteString("change", change);
            writer.WriteString("author", SkillAuthor.Agent.Name);
            writer.WriteString("description", draft.Description);
            writer.WriteString("replay", replay.Describe());
            writer.WriteString("body", draft.Body.Length > 1_000 ? draft.Body[..1_000] + "…" : draft.Body);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var state = new TurnStateBuilder().Build(new TurnStateInput(request, "skills", _options.Generator));
        var result = await _fabric.DecideAsync(sessionId, Checkpoint.PreExecute, state,
            [KeyValuePair.Create("skill_write_safe", question.ToQuestion(instructions: DecisionContent.FromJson(document.RootElement.Clone())))],
            new HashSet<string>(StringComparer.Ordinal) { FabricConditions.AgentAuthored }, cancellationToken).ConfigureAwait(false);
        return result["skill_write_safe"];
    }

    // ---- review (/skills pending, approve, reject) ----

    public IReadOnlyList<PendingSkill> Pending() => _database.Pending();

    /// <summary>
    /// Applies a staged draft as the agent's, after the user's review. The skill must still be new or wholly
    /// agent-authored: a review never lets the loop overwrite a skill the user or a hub wrote since.
    /// </summary>
    public async Task<string?> ApproveAsync(string pendingId, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var pending = _database.FindPending(pendingId) is { Status: PendingSkillStatus.Pending } found ? found
            : throw new ArgumentException($"No draft '{pendingId}' is waiting for review.", nameof(pendingId));
        var draft = SkillDocument.Parse(pending.Content);
        var revision = await _store.WriteAgentSkillAsync(draft, sessionId ?? pending.SessionId, "Add skill " + draft.Name + " (approved by the user)",
            cancellationToken).ConfigureAwait(false);
        if (!_database.Resolve(pendingId, PendingSkillStatus.Approved))
            throw new InvalidOperationException($"'{pendingId}' was resolved meanwhile.");
        _fabric.RecordOutcome(pending.SessionId, pending.DecisionId, "approved", cancellationToken);
        if (pending.OpportunityId is { } opportunity)
            _database.SetState(opportunity, OpportunityState.Applied, "approved by the user");
        await RunEvalsAsync(draft.Name, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public void Reject(string pendingId, CancellationToken cancellationToken = default)
    {
        var pending = _database.FindPending(pendingId) is { Status: PendingSkillStatus.Pending } found ? found
            : throw new ArgumentException($"No draft '{pendingId}' is waiting for review.", nameof(pendingId));
        if (!_database.Resolve(pendingId, PendingSkillStatus.Rejected))
            throw new InvalidOperationException($"'{pendingId}' was resolved meanwhile.");
        _fabric.RecordOutcome(pending.SessionId, pending.DecisionId, "rejected", cancellationToken);
        if (pending.OpportunityId is { } opportunity)
            _database.SetState(opportunity, OpportunityState.Failed, "rejected by the user");
    }

    private async Task RunEvalsAsync(string skill, CancellationToken cancellationToken)
    {
        if (_curator is null)
            return;
        try
        {
            await _curator.RunEvalsAsync(skill, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Eval results are reported, not enforced here; a failure to run them never undoes the change.
        }
    }

    // ---- drafting ----

    internal string DraftPrompt(IReadOnlyList<StoredEvent> turn)
    {
        var text = new StringBuilder();
        text.AppendLine("The assistant just completed the task below. Write a skill that would let it do tasks like this again.");
        text.AppendLine("The SKILL.md starts with a frontmatter block:");
        text.AppendLine("---");
        text.AppendLine("name: lowercase-words-with-hyphens");
        text.AppendLine("description: One sentence saying when to use this skill.");
        text.AppendLine("---");
        text.AppendLine("Then numbered steps in Markdown, naming each tool to call, in the order used. Do not include secrets, personal data or one-off values.");
        text.AppendLine();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in turn)
        {
            switch (item.Payload)
            {
                case UserMessage user:
                    text.Append("User: ").AppendLine(user.Text);
                    break;
                case ToolCallIssued call:
                    names[call.CallId] = call.ToolName;
                    text.Append("Tool call: ").Append(call.ToolName).Append(' ').AppendLine(Cut(call.Arguments.GetRawText()));
                    break;
                case ToolResult result:
                    text.Append(result.IsError ? "Tool error (" : "Tool result (").Append(names.GetValueOrDefault(result.CallId, "tool")).Append("): ")
                        .AppendLine(Cut(result.Result.ValueKind == JsonValueKind.String ? result.Result.GetString()! : result.Result.GetRawText()));
                    break;
                case AssistantMessage assistant:
                    text.Append("Assistant: ").AppendLine(assistant.Text);
                    break;
            }
        }
        return text.ToString();

        string Cut(string value) => value.Length > _options.ResultChars
            ? value[.._options.ResultChars] + string.Create(CultureInfo.InvariantCulture, $"… [{value.Length - _options.ResultChars} more]")
            : value;
    }

    /// <summary>The SKILL.md inside a reply: from the first frontmatter fence, without a surrounding code block.</summary>
    internal static string ExtractSkill(string reply)
    {
        var text = reply.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var start = text.StartsWith("---\n", StringComparison.Ordinal) ? 0 : text.IndexOf("\n---\n", StringComparison.Ordinal) + 1;
        if (start < 0 || start >= text.Length)
            return text;
        text = text[start..];
        var fence = text.LastIndexOf("\n```", StringComparison.Ordinal);
        return fence > 0 ? text[..fence] + "\n" : text + "\n";
    }
}
