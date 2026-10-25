using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models.Privacy;
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

    /// <summary>Characters of each event's text the drafting prompt shows: a message, a call's arguments or a result.</summary>
    public int EventChars { get; init; } = 1_200;

    /// <summary>Events from the start of the source turn the drafting prompt shows.</summary>
    public int HeadEvents { get; init; } = 40;

    /// <summary>Events from the end of the source turn the drafting prompt shows; the middle is marked as omitted.</summary>
    public int TailEvents { get; init; } = 40;

    public int MaxOutputTokens { get; init; } = 1_024;
}

/// <summary>
/// The learning loop (plan §11): <c>skill_opportunity</c> queues a turn (<see cref="Queue"/>); a background job
/// (<see cref="RunAsync"/>, on the device scheduler through the generator it is given) drafts a skill from the turn's
/// events with the generator; the draft must be well formed, pass the scanner and pass its replay test
/// (<see cref="SkillReplayer"/>); then <c>skill_write_safe</c> gates it at the pre-execute checkpoint. Only a calibrated
/// answer above the floor, on a skill the agent wrote (the <c>agent_authored</c> condition), applies it; otherwise the
/// draft is staged for <c>/skills pending</c>, as it is when the source turn was tainted or its <c>task_succeeded</c> answer
/// was not calibrated (C-026). The degraded decider never answers a gate, so without Kev every draft is staged, and a draft
/// never touches a skill the user or a hub wrote, approved or not.
/// </summary>
public sealed class SkillLearner : ISkillLearning
{
    /// <summary>Its fixed system prompt, Corvid's own text (A5: the egress gate neither scans nor rewrites it).</summary>
    public const string SystemPrompt =
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.EventChars, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.HeadEvents, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.TailEvents, nameof(options));
    }

    /// <summary>Raised after each opportunity is processed, for a surface to mention.</summary>
    public event Action<LearningResult>? Processed;

    public void Queue(SkillOpportunity opportunity)
    {
        ArgumentNullException.ThrowIfNull(opportunity);
        _database.Enqueue(opportunity.SessionId, opportunity.FromSequence, opportunity.ThroughSequence, opportunity.DecisionId, opportunity.Request,
            opportunity.Tainted, opportunity.SuccessDecisionId, opportunity.SuccessCalibrated);
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

        // The turn is read page by page, twice: once for the recovery scan, once into the drafting window.
        IEnumerable<StoredEvent> Turn() =>
            SessionProjection.ReadRange(_log, opportunity.SessionId, opportunity.FromSequence - 1, opportunity.ThroughSequence, cancellationToken);
        if (_log.Read(opportunity.SessionId, opportunity.FromSequence - 1, opportunity.ThroughSequence, 1, cancellationToken).Count == 0)
            return Fail("the source turn is not in the log");
        var scan = ToolRecovery.Scan(Turn());
        // /undo or /rollback (C-014) after the turn was queued: the user took it back, so it is no procedure to learn from.
        var session = SessionProjection.Load(_log, opportunity.SessionId, cancellationToken: cancellationToken);
        if (!session.IsVisible(opportunity.FromSequence) || !session.IsVisible(opportunity.ThroughSequence))
            return Fail("the source turn was undone, so it is not distilled");

        // The draft and the replay send the source turn to the background generator, which may be a cloud binding: its gate
        // and Vault see the source session and its taint, which the replay's own clean session cannot lower (plan §10.3).
        using var scope = new ModelRequestContext(opportunity.SessionId, opportunity.Tainted).Enter();

        // 2. Draft (System Two work).
        var response = await _drafter.GetResponseAsync(
            [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, DraftPrompt(Turn(), scan))],
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
        // What the draft is made against: a review applies an edited draft only over this revision (or, for a new skill, none).
        var stagedAgainst = await _store.RevisionOfAsync(draft.Name, cancellationToken).ConfigureAwait(false);

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
        // the user reviews such a draft. So does one whose task success no calibrated decider confirmed (C-026): the degraded
        // tier's yes, or an egress checkpoint that fell back to it, is no evidence the procedure worked.
        if (verdict.Verdict.Acted && !opportunity.Tainted && opportunity.SuccessCalibrated)
        {
            var revision = await _store.WriteAgentSkillAsync(draft, opportunity.SessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await RunEvalsAsync(draft.Name, cancellationToken).ConfigureAwait(false);
            return new LearningResult(opportunity.Id, LearningStatus.Applied, draft.Name, $"applied as {revision?[..12] ?? "unchanged"}; replay {replay.Describe()}");
        }
        var reason = opportunity.Tainted ? "the source turn held outside content, so the draft waits for review"
            : !verdict.Verdict.Acted ? verdict.Verdict.Reason
            : "task success was not confirmed by a calibrated decider, so the draft waits for review";
        var pending = _database.Stage(draft.Name, draft.Text, opportunity.SessionId, verdict.DecisionId, reason, replay.Describe(), opportunity.Id, stagedAgainst);
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
        RecordReview(pending, SkillApproved, cancellationToken);
        if (pending.OpportunityId is { } opportunity)
            _database.SetState(opportunity, OpportunityState.Applied, "approved by the user");
        await RunEvalsAsync(draft.Name, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    /// <summary>
    /// Applies a staged draft the user edited first: <paramref name="editedContent"/> is written instead of the draft, as the
    /// user's (the person's text, which the loop and the curator then leave alone), and the <c>skill_write_safe</c> decision
    /// records <c>corrected</c>. The edit must still name the draft's skill and pass the scanner. Content equal to the draft's
    /// is a plain <see cref="ApproveAsync(string, string?, CancellationToken)"/>.
    /// </summary>
    /// <exception cref="SkillFormatException">The edit is not a valid SKILL.md.</exception>
    /// <exception cref="ArgumentException">The draft is not waiting, the edit names another skill, or the scanner flags it.</exception>
    /// <exception cref="SkillOwnershipException">The skill changed since the draft was staged (the draft stays waiting).</exception>
    public async Task<string?> ApproveAsync(string pendingId, string? sessionId, string? editedContent, CancellationToken cancellationToken)
    {
        var pending = _database.FindPending(pendingId) is { Status: PendingSkillStatus.Pending } found ? found
            : throw new ArgumentException($"No draft '{pendingId}' is waiting for review.", nameof(pendingId));
        if (editedContent is null || string.Equals(editedContent, pending.Content, StringComparison.Ordinal))
            return await ApproveAsync(pendingId, sessionId, cancellationToken).ConfigureAwait(false);
        var edited = SkillDocument.Parse(editedContent);
        if (!string.Equals(edited.Name, pending.Skill, StringComparison.Ordinal))
            throw new ArgumentException($"The edited SKILL.md names '{edited.Name}', not {pending.Skill}, the skill this draft writes.", nameof(editedContent));
        if (SkillScanner.Scan(SkillDocument.FileName, editedContent) is { IsEmpty: false } findings)
            throw new ArgumentException("The scanner flagged the edit: " + string.Join("; ",
                findings.Take(5).Select(finding => $"line {finding.Line} {finding.Rule}")) + ".", nameof(editedContent));
        // Only over what the draft was staged against: a skill the person or a hub changed since stays as it is.
        var revision = await _store.WriteReviewedSkillAsync(edited, pending.BaseRevision, sessionId ?? pending.SessionId,
            "Add skill " + edited.Name + " (a draft the user edited and approved)", cancellationToken).ConfigureAwait(false);
        if (!_database.Resolve(pendingId, PendingSkillStatus.Approved))
            throw new InvalidOperationException($"'{pendingId}' was resolved meanwhile.");
        _fabric.RecordOutcome(pending.SessionId, pending.DecisionId, "corrected", cancellationToken);
        RecordReview(pending, SkillApproved, cancellationToken);
        if (pending.OpportunityId is { } opportunity)
            _database.SetState(opportunity, OpportunityState.Applied, "approved by the user with edits");
        await RunEvalsAsync(edited.Name, cancellationToken).ConfigureAwait(false);
        return revision;
    }

    public void Reject(string pendingId, CancellationToken cancellationToken = default) => Reject(pendingId, null, cancellationToken);

    /// <summary>Rejects a staged draft; <paramref name="reason"/>, when given, is recorded on the opportunity it came from.</summary>
    public void Reject(string pendingId, string? reason, CancellationToken cancellationToken)
    {
        var pending = _database.FindPending(pendingId) is { Status: PendingSkillStatus.Pending } found ? found
            : throw new ArgumentException($"No draft '{pendingId}' is waiting for review.", nameof(pendingId));
        if (!_database.Resolve(pendingId, PendingSkillStatus.Rejected))
            throw new InvalidOperationException($"'{pendingId}' was resolved meanwhile.");
        _fabric.RecordOutcome(pending.SessionId, pending.DecisionId, "rejected", cancellationToken);
        RecordReview(pending, SkillRejected, cancellationToken);
        if (pending.OpportunityId is { } opportunity)
            _database.SetState(opportunity, OpportunityState.Failed,
                string.IsNullOrWhiteSpace(reason) ? "rejected by the user" : "rejected by the user: " + reason.Trim());
    }

    /// <summary>The outcome a review records on the egress decisions that queued the draft (C-026).</summary>
    public const string SkillApproved = "skill_approved";

    /// <inheritdoc cref="SkillApproved"/>
    public const string SkillRejected = "skill_rejected";

    // The review is indirect evidence for the turn's skill_opportunity and task_succeeded decisions: distinct outcome strings
    // let calibration (C-021) count or drop it apart from the direct approved/rejected on skill_write_safe.
    private void RecordReview(PendingSkill pending, string outcome, CancellationToken cancellationToken)
    {
        if (pending.OpportunityId is null || _database.FindOpportunity(pending.OpportunityId) is not { } source)
            return;
        _fabric.RecordOutcome(pending.SessionId, source.DecisionId, outcome, cancellationToken);
        if (source.SuccessDecisionId is { } success)
            _fabric.RecordOutcome(pending.SessionId, success, outcome, cancellationToken);
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

    /// <summary>
    /// The drafting prompt: the source turn as a head-and-tail window (C-026, after Beacon's evaluator projection). The first
    /// <see cref="SkillLearnerOptions.HeadEvents"/> and last <see cref="SkillLearnerOptions.TailEvents"/> events are shown, with
    /// a marker counting the events omitted between them, and each event's text is cut to
    /// <see cref="SkillLearnerOptions.EventChars"/>, so a long turn keeps both how it started and how it ended.
    /// </summary>
    internal string DraftPrompt(IEnumerable<StoredEvent> turn, ToolRecoveryScan? scan = null)
    {
        var text = new StringBuilder();
        text.AppendLine("The assistant just completed the task below. Write a skill that would let it do tasks like this again.");
        text.AppendLine("The SKILL.md starts with a frontmatter block:");
        text.AppendLine("---");
        text.AppendLine("name: lowercase-words-with-hyphens");
        text.AppendLine("description: One sentence saying when to use this skill.");
        text.AppendLine("---");
        text.AppendLine("Then numbered steps in Markdown, naming each tool to call, in the order used. Do not include secrets, personal data or one-off values.");
        if (scan?.Recoveries.FirstOrDefault() is { } recovery)
            text.Append("The turn recovered: ").Append(recovery.FailedTool).Append(" failed, then ").Append(recovery.RecoveredTool)
                .AppendLine(" succeeded; write the skill around the approach that worked.");
        text.AppendLine();

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var head = new List<string>(_options.HeadEvents);
        var tail = new Queue<string>(_options.TailEvents);
        var omitted = 0;
        foreach (var item in turn)
        {
            if (Render(item.Payload) is not { } line)
                continue;
            if (head.Count < _options.HeadEvents)
            {
                head.Add(line);
                continue;
            }
            if (tail.Count == _options.TailEvents)
            {
                tail.Dequeue();
                omitted++;
            }
            tail.Enqueue(line);
        }
        foreach (var line in head)
            text.AppendLine(line);
        if (omitted > 0)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"[… {omitted} events omitted from the middle of this turn …]"));
        foreach (var line in tail)
            text.AppendLine(line);
        return text.ToString();

        string? Render(SessionEvent payload)
        {
            switch (payload)
            {
                // A gateway guest's message is outside content, quoted as theirs as the prompt quotes it (A4 verification).
                case UserMessage { From: { } guest } user:
                    return Corvid.Kernel.Turns.PromptRenderer.Quoted(guest, Cut(user.Text));
                case UserMessage user:
                    return "User: " + Cut(user.Text);
                case ToolCallIssued call:
                    names[call.CallId] = call.ToolName;
                    return $"Tool call: {call.ToolName} {Cut(call.Arguments.GetRawText())}";
                case ToolResult result:
                    var value = result.Result.ValueKind == JsonValueKind.String ? result.Result.GetString()! : result.Result.GetRawText();
                    return $"{(result.IsError ? "Tool error" : "Tool result")} ({names.GetValueOrDefault(result.CallId, "tool")}): {Cut(value)}";
                case AssistantMessage assistant:
                    return "Assistant: " + Cut(assistant.Text);
                default:
                    return null;
            }
        }

        string Cut(string value) => value.Length > _options.EventChars
            ? value[.._options.EventChars] + string.Create(CultureInfo.InvariantCulture, $"… [{value.Length - _options.EventChars} more]")
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
