using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Fabric;
using Corvid.Host.Composition;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Events;

namespace Corvid.Host.Sessions;

/// <summary>What a replay includes besides the conversation itself.</summary>
/// <param name="LedgerDiffs">Each finished file edit's diff from the write ledger, as a live turn reports it.</param>
/// <param name="Plans">The task list as it changed (<see cref="PlanChanged"/>).</param>
/// <param name="Checkpoints">The write ledger's checkpoints (<see cref="CheckpointLogged"/>).</param>
/// <param name="Approvals">
/// Each asked call's approval answer (<see cref="ToolCallChanged.Approval"/>), and a refused call's outcome as
/// <c>denied</c>, as a live turn shows it; for a call that asked nobody, the pipeline's assessment
/// (<see cref="ToolCallChanged.Assessment"/>, G-2) or the tool's read-only declaration; and the checkpoint taken before it.
/// </param>
/// <param name="UnfinishedCalls">
/// A call the log holds no result for (its turn was interrupted, or the process stopped under it) replays as failed and
/// cancelled, as the live turn closed it, rather than as completed.
/// </param>
/// <param name="Steps">Each step a turn began, from its calls' rounds (<see cref="TurnStepped"/>), as a live turn reports it.</param>
/// <param name="Questions">The clarifying question a turn ended on (K-6, <see cref="QuestionRaised"/>).</param>
/// <param name="UnfinishedAsRunning">
/// With <paramref name="UnfinishedCalls"/> off, a call the log holds no result for replays as running rather than as
/// completed: another process is still working on it (G-12's tail of a session that process drives).
/// </param>
/// <param name="After">Only what the log holds past this position: the next part of a session read before (G-12's tail).</param>
/// <param name="Decisions">Each checkpoint's decisions (<see cref="DecisionsMade"/>), from the <c>Decision</c> events.</param>
/// <param name="Generators">Each turn's generator on its <see cref="PromptSent"/>, from its first decision's TurnState.</param>
/// <param name="Taint">Where outside content entered and where the user's next message cleared it (<see cref="TaintNoted"/>).</param>
/// <param name="Compactions">Each compaction's marker and record (<see cref="CompactionLogged"/>).</param>
public sealed record ReplayOptions(bool LedgerDiffs = true, bool Plans = true, bool Checkpoints = true, bool Approvals = true,
    bool UnfinishedCalls = true, bool Steps = true, bool Questions = true, bool UnfinishedAsRunning = false, long After = 0,
    bool Decisions = true, bool Generators = true, bool Taint = true, bool Compactions = true)
{
    /// <summary>A5: each <c>ModelSwitched</c> as a <see cref="ModelChanged"/>, by position. Off unless asked for.</summary>
    public bool Models { get; init; }

    /// <summary>A5: each <c>ChildSpawned</c> and <c>ChildFinished</c> as <see cref="ChildStarted"/> and <see cref="ChildEnded"/>. Off unless asked for.</summary>
    public bool Children { get; init; }

    /// <summary>
    /// A5: the generator's tokens the log records on an answer or a response's first call, as <see cref="UsageReported"/>
    /// just before it (where the live turn reported them). On in <see cref="Full"/>, off in <see cref="Transcript"/>.
    /// </summary>
    public bool Usage { get; init; } = true;

    /// <summary>Everything a live turn reports that the log holds.</summary>
    public static ReplayOptions Full { get; } = new();

    /// <summary>
    /// The conversation alone: messages, calls and their results as text (ACP's <c>session/load</c>, whose recorded
    /// exchanges keep a call without a result as completed).
    /// </summary>
    public static ReplayOptions Transcript { get; } = new(LedgerDiffs: false, Plans: false, Checkpoints: false, Approvals: false, UnfinishedCalls: false,
        Steps: false, Questions: false, Decisions: false, Generators: false, Taint: false, Compactions: false) { Usage = false };
}

/// <summary>A session read back from the log: its updates in order, and the log head they run to.</summary>
/// <param name="Head">The last position read: a live report from here on continues the replay exactly.</param>
public sealed record SessionHistory(string SessionId, long Head, ImmutableArray<SessionStreamUpdate> Updates);

/// <summary>
/// A stored session as the same updates a live turn reports (K-10): the conversation as it stands (compactions expanded,
/// undone turns left out), so a resumed session and a live one look alike on every surface. With
/// <see cref="ReplayOptions.Full"/> it also carries what a live turn shows beside the conversation, as far as the log holds
/// it: each checkpoint's decisions (grouped from the <c>Decision</c> events), each turn's generator (from its first
/// decision's TurnState), an asked call's approval answer and assessment, where a network read tainted the session, each
/// compaction's marker, the steers (K-3) in the turn that took them, a clarifying question (K-6) after the answer that
/// carries it, and each step from its calls' round, and (A5) the generator's token usage where the log records it. The log has
/// no record of how a turn ended: a turn with no answer was interrupted, cut by a rollback or stopped with its process, and a
/// surface shows it as interrupted. Thoughts and running tails (K-2) are live only.
/// </summary>
public static class SessionReplay
{
    /// <summary>Reads a session from the log through its head (or <paramref name="through"/>).</summary>
    public static async Task<SessionHistory> ReadAsync(CorvidRuntime runtime, string sessionId, ToolPresentation tools,
        ReplayOptions? options = null, long? through = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(tools);
        options ??= ReplayOptions.Full;
        // A6 (G-4): calls are shown in the workspace the session works in.
        tools = tools.ForSessionOrSelf(sessionId);
        var head = through ?? runtime.Log.GetHead(sessionId, cancellationToken);
        var snapshot = SessionProjection.Load(runtime.Log, sessionId, head, expandCompactions: true, cancellationToken);
        // Each transcript entry's time is its event's: read once, by position, with the plans and checkpoints between them.
        var stored = SessionProjection.ReadRange(runtime.Log, sessionId, 0, head, cancellationToken).ToDictionary(item => item.Sequence);
        var results = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolResult>()
            .GroupBy(result => result.CallId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var calls = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolCallIssued>()
            .GroupBy(call => call.CallId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        // When each call was issued: a result's time less its call's is how long it took, as the live turn reports it.
        var issuedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Transcript)
            if (entry.Content is ToolCallIssued { CallId: var issuedId } && stored.TryGetValue(entry.FromSequence, out var issuedItem))
                issuedAt[issuedId] = issuedItem.RecordedAt;
        var checkpoints = snapshot.CheckpointEvents.Where(item => snapshot.IsVisible(item.Sequence)).ToList();
        var approvals = options.Approvals ? Approvals(stored.Values, snapshot.IsVisible) : [];
        // G-2: why a call that asked nobody ran, or was refused.
        var assessments = options.Approvals
            ? stored.Values.Where(item => snapshot.IsVisible(item.Sequence)).OrderBy(item => item.Sequence).Select(item => item.Payload)
                .OfType<ApprovalAssessed>().GroupBy(assessed => assessed.CallId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal)
            : [];
        // Visible requests only, as the assessments: a call id asked in an undone turn says nothing of the call shown now.
        var asked = options.Approvals
            ? stored.Values.Where(item => snapshot.IsVisible(item.Sequence)).Select(item => item.Payload).OfType<ApprovalRequested>()
                .Select(request => request.CallId).OfType<string>()
                .ToHashSet(StringComparer.Ordinal)
            : [];
        var generators = options.Generators ? Generators(stored.Values, snapshot.IsVisible) : [];
        var tainted = false;

        var items = new List<(long Sequence, SessionStreamUpdate Update)>();
        int? round = null;
        foreach (var entry in snapshot.Transcript)
        {
            if (entry.FromSequence <= options.After)
            {
                // Read before: its step was reported then, so a later call of the same step starts none.
                round = entry.Content switch
                {
                    UserMessage => null,
                    ToolCallIssued { Round: { } step and > 0 } => step,
                    _ => round,
                };
                // And its taint was reported then: a later message clears it, a later network read adds none.
                if (entry.Content is UserMessage)
                    tainted = false;
                else if (entry.Content is ToolResult { CallId: var readId } && calls.GetValueOrDefault(readId) is { } read && tools.Network(read.ToolName))
                    tainted = true;
                continue;
            }
            var at = stored.TryGetValue(entry.FromSequence, out var item) ? item.RecordedAt : default;
            switch (entry.Content)
            {
                case UserMessage user:
                    items.Add((entry.FromSequence, new PromptSent(user.Text, entry.FromSequence)
                    {
                        At = at,
                        From = user.From,
                        Images = user.Images ?? [],
                        Generator = generators.GetValueOrDefault(entry.FromSequence),
                    }));
                    round = null;
                    // The user's own message clears taint (plan §10.3), as the live turn reports when it begins. The log does not
                    // say whose message it was, so a guest's or a job's (which keeps the taint) reads as the user's.
                    if (options.Taint && tainted)
                    {
                        tainted = false;
                        items.Add((entry.FromSequence, new TaintNoted(false, null) { At = at }));
                    }
                    break;
                // K-3: a steer is part of the turn that took it.
                case UserSteered steer:
                    items.Add((entry.FromSequence, new SteerSent(steer.Text, entry.FromSequence) { At = at }));
                    break;
                case AssistantMessage assistant:
                    if (options.Usage && assistant.Usage is { } answered)
                        items.Add((entry.FromSequence, Used(answered, at)));
                    items.Add((entry.FromSequence, new AnswerChunk(assistant.Text) { At = at }));
                    break;
                case ToolCallIssued call:
                    if (options.Usage && call.Usage is { } issuedUsage)
                        items.Add((entry.FromSequence, Used(issuedUsage, at)));
                    // The step its round records, as the live turn reported it when the step began (with no budget: the log has none).
                    if (options.Steps && call.Round is { } step and > 0 && step != round)
                    {
                        round = step;
                        items.Add((entry.FromSequence, new TurnStepped(step, null) { At = at, Generator = call.Generator }));
                    }
                    var finished = results.TryGetValue(call.CallId, out var outcome);
                    var unfinished = !finished && options.UnfinishedCalls;
                    var failed = (finished && outcome!.IsError) || unfinished;
                    var running = !finished && !options.UnfinishedCalls && options.UnfinishedAsRunning;
                    items.Add((entry.FromSequence, new ToolCallStarted(call.CallId, ToolPresentation.Title(call.ToolName, call.Arguments),
                        tools.Kind(call.ToolName), failed ? ToolCallState.Failed : running ? ToolCallState.InProgress : ToolCallState.Completed)
                    {
                        At = at,
                        Tool = call.ToolName,
                        Input = call.Arguments,
                        Locations = tools.Locations(call.Arguments),
                        Generator = call.Generator,
                    }));
                    // No result in the log: the call never finished. A live turn closes it as cancelled when its turn is
                    // interrupted, and so does the replay.
                    if (unfinished)
                        items.Add((entry.FromSequence, new ToolCallChanged(call.CallId)
                        {
                            At = at,
                            Tool = call.ToolName,
                            Kind = tools.Kind(call.ToolName),
                            Status = ToolCallState.Failed,
                            Outcome = "cancelled",
                        }));
                    break;
                case ToolResult result:
                    IReadOnlyList<FileDiff> diffs = [];
                    if (options.LedgerDiffs
                        && checkpoints.FirstOrDefault(c => c.Checkpoint is { Kind: CheckpointKinds.Before } b && b.CallId == result.CallId) is { } before
                        && checkpoints.FirstOrDefault(c => c.Checkpoint is { Kind: CheckpointKinds.After } a && a.CallId == result.CallId) is { Checkpoint.Paths: { Length: > 0 } paths } after)
                        diffs = await tools.LedgerDiffsAsync(before.Checkpoint.Revision, after.Checkpoint.Revision, paths).ConfigureAwait(false);
                    var approval = approvals.GetValueOrDefault(result.CallId);
                    var checkpoint = options.Approvals
                        ? checkpoints.FirstOrDefault(c => c.Checkpoint is { Kind: CheckpointKinds.Before } b && b.CallId == result.CallId)?.Checkpoint.CheckpointId
                        : null;
                    items.Add((entry.FromSequence, new ToolCallChanged(result.CallId)
                    {
                        Assessment = assessments.GetValueOrDefault(result.CallId),
                        ReadOnly = options.Approvals && !asked.Contains(result.CallId) ? calls.GetValueOrDefault(result.CallId)?.ReadOnly : null,
                        CheckpointBefore = checkpoint,
                        At = at,
                        Tool = calls.GetValueOrDefault(result.CallId)?.ToolName,
                        Kind = calls.GetValueOrDefault(result.CallId) is { } issued ? tools.Kind(issued.ToolName) : null,
                        Status = result.IsError ? ToolCallState.Failed : ToolCallState.Completed,
                        // A refused approval is a denied call, as the kernel's span says live; its result is the refusal.
                        Outcome = approval is { Approved: false } && result.IsError ? "denied" : null,
                        Approval = approval,
                        Output = result.Result,
                        OutputText = ToolPresentation.ResultText(result.Result),
                        Diffs = [.. diffs],
                        Elapsed = issuedAt.TryGetValue(result.CallId, out var started) && started != default && at >= started ? at - started : null,
                    }));
                    // A network read's result is outside content: the session is tainted from here until the user speaks.
                    if (options.Taint && !tainted && calls.GetValueOrDefault(result.CallId) is { } read && tools.Network(read.ToolName))
                    {
                        tainted = true;
                        items.Add((entry.FromSequence, new TaintNoted(true, $"{read.ToolName} {ToolPresentation.Target(read.Arguments)}".Trim()) { At = at }));
                    }
                    break;
            }
        }

        // K-6: each clarifying question after the answer that carries it, as the live turn reported it; A5: a question
        // answered inside its turn, closed where the answer was logged.
        var questions = false;
        foreach (var item in stored.Values.Where(item => options.Questions && item.Payload is ClarificationAnswered && item.Sequence > options.After
            && snapshot.IsVisible(item.Sequence)))
        {
            questions = true;
            var answered = (ClarificationAnswered)item.Payload;
            items.Add((item.Sequence, new QuestionClosed(answered.QuestionId, answered.Answer) { At = item.RecordedAt }));
        }
        foreach (var item in stored.Values.Where(item => options.Questions && item.Payload is ClarificationAsked && item.Sequence > options.After
            && snapshot.IsVisible(item.Sequence)))
        {
            questions = true;
            var question = (ClarificationAsked)item.Payload;
            items.Add((item.Sequence, new QuestionRaised(question.QuestionId, question.Text, question.Options.IsDefault ? [] : question.Options)
            {
                At = item.RecordedAt,
            }));
        }
        if (options.Decisions)
            items.AddRange(Decisions(stored.Values, snapshot.IsVisible, runtime.Fabric.Policy.Catalogue).Where(item => item.Sequence > options.After));
        if (options.Compactions)
            items.AddRange(Compactions(stored.Values, snapshot).Where(item => item.Sequence > options.After));

        if (options.Plans)
            foreach (var item in stored.Values.Where(item => item.Payload is TodosSet && item.Sequence > options.After && snapshot.IsVisible(item.Sequence)))
                items.Add((item.Sequence, new PlanChanged(((TodosSet)item.Payload).Items) { At = item.RecordedAt }));
        if (options.Checkpoints)
            foreach (var checkpoint in checkpoints.Where(checkpoint => checkpoint.Sequence > options.After))
                items.Add((checkpoint.Sequence, new CheckpointLogged(checkpoint.Checkpoint, checkpoint.Sequence)
                {
                    At = stored.TryGetValue(checkpoint.Sequence, out var item) ? item.RecordedAt : default,
                }));

        // A5: model switches and children, by position.
        foreach (var item in stored.Values.Where(item => item.Sequence > options.After && snapshot.IsVisible(item.Sequence)))
        {
            switch (item.Payload)
            {
                case ModelSwitched switched when options.Models:
                    items.Add((item.Sequence, ModelChanges.FromLog(switched, item.Sequence) with { At = item.RecordedAt }));
                    break;
                case ChildSpawned spawned when options.Children:
                    items.Add((item.Sequence, new ChildStarted(spawned, item.Sequence) { At = item.RecordedAt }));
                    break;
                case ChildFinished finished when options.Children:
                    items.Add((item.Sequence, new ChildEnded(finished, item.Sequence) { At = item.RecordedAt }));
                    break;
            }
        }

        // The transcript in its own order; plans and checkpoints placed among it by position (a stable sort, so the
        // transcript's order holds for entries at one position).
        var ordered = options.Plans || options.Checkpoints || options.Decisions || options.Compactions || questions || options.Models || options.Children
            ? items.OrderBy(item => item.Sequence).ToList()
            : items;
        return new SessionHistory(sessionId, head, [.. ordered.Select(item => item.Update)]);
    }

    private static UsageReported Used(TokenUsage usage, DateTimeOffset at) =>
        new(usage.InputTokens, usage.OutputTokens) { At = at, PromptTokens = usage.PromptTokens, Calls = usage.Calls };

    /// <summary>
    /// Each call's latest closed approval, by call id: a visible <c>ApprovalRequested</c> naming the call, and the
    /// <c>ApprovalResolved</c> that closed it. A prompt outside any call (a memory write) has no card to put it on.
    /// </summary>
    private static Dictionary<string, CallApproval> Approvals(IEnumerable<StoredEvent> stored, Func<long, bool> visible)
    {
        var asked = new Dictionary<string, string>(StringComparer.Ordinal);
        var answers = new Dictionary<string, CallApproval>(StringComparer.Ordinal);
        foreach (var item in stored.OrderBy(item => item.Sequence))
        {
            if (!visible(item.Sequence))
                continue;
            switch (item.Payload)
            {
                case ApprovalRequested { CallId: { } callId } request:
                    asked[request.ApprovalId] = callId;
                    break;
                case ApprovalResolved resolved when asked.Remove(resolved.ApprovalId, out var call):
                    answers[call] = new CallApproval(resolved.ApprovalId, resolved.Approved, resolved.Reason)
                    {
                        Scope = resolved.Scope ?? ScopeOf(resolved.Reason),
                        Note = resolved.Note,
                    };
                    break;
            }
        }
        return answers;
    }

    /// <summary>
    /// Each turn's generator, by its user message's position: the <c>generator</c> field of the TurnState its first decision
    /// was asked with ("qwen3.5-0.8b local, 8k" gives "qwen3.5-0.8b"). A state cut to fit its budget ("qwen3.5-0.…") names
    /// no model, and a turn with no decision (a job's script) has none.
    /// </summary>
    private static Dictionary<long, string> Generators(IEnumerable<StoredEvent> stored, Func<long, bool> visible)
    {
        var generators = new Dictionary<long, string>();
        long? turn = null;
        foreach (var item in stored.OrderBy(item => item.Sequence))
        {
            if (!visible(item.Sequence))
                continue;
            switch (item.Payload)
            {
                case UserMessage:
                    turn = item.Sequence;
                    break;
                case Decision { State: { ValueKind: JsonValueKind.Object } state } when turn is { } at && !generators.ContainsKey(at):
                    if (state.TryGetProperty("generator", out var described) && described.ValueKind == JsonValueKind.String
                        && GeneratorName(described.GetString()) is { } name)
                        generators[at] = name;
                    break;
            }
        }
        return generators;
    }

    /// <summary>The model a TurnState's generator line names ("qwen3.5-0.8b@2 local, 8k" gives "qwen3.5-0.8b"); null when cut short.</summary>
    internal static string? GeneratorName(string? described)
    {
        if (string.IsNullOrWhiteSpace(described))
            return null;
        var space = described.IndexOf(' ', StringComparison.Ordinal);
        // No space: the line was cut inside the model's id, which then names no model.
        if (space <= 0)
            return null;
        var id = described[..space];
        var at = id.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? id[..at] : id;
    }

    /// <summary>
    /// The decisions in the log as the checkpoints that asked them: one checkpoint appends its questions together, in key
    /// order, all asked over one TurnState, so a run of <c>Decision</c> events with the same state and no question twice is
    /// one checkpoint. Its name is the checkpoint every question of the run is asked at; for a run whose questions are asked
    /// at more than one (the <c>tool</c> question alone), the turn's first run is ingress and a later one a step. An answer
    /// acted when its action is not the question's below-floor one.
    /// </summary>
    private static IEnumerable<(long Sequence, SessionStreamUpdate Update)> Decisions(IEnumerable<StoredEvent> stored, Func<long, bool> visible,
        QuestionCatalogue catalogue)
    {
        var run = new List<(StoredEvent Item, Decision Decision)>();
        var firstInTurn = true;
        var output = new List<(long, SessionStreamUpdate)>();

        void Flush()
        {
            if (run.Count == 0)
                return;
            var checkpoints = Shared(run.Select(pair => pair.Decision.QuestionId), catalogue);
            // A question the catalogue no longer has names no checkpoint: the first one its neighbours are asked at, or a step.
            if (checkpoints.Count == 0)
                checkpoints = [.. run.SelectMany(pair => catalogue.TryResolve(pair.Decision.QuestionId, out var known) ? known.Checkpoints : []).Take(1)];
            if (checkpoints.Count == 0)
                checkpoints = [Checkpoint.Step];
            var checkpoint = checkpoints.Count == 1 ? checkpoints[0]
                : checkpoints.Contains(Checkpoint.Ingress) && firstInTurn ? Checkpoint.Ingress
                : checkpoints.Contains(Checkpoint.Step) ? Checkpoint.Step
                : checkpoints.Count > 0 ? checkpoints[0] : (Checkpoint?)null;
            var decider = run.Select(pair => pair.Decision.Decider).FirstOrDefault(name => name is { Length: > 0 } && !name.StartsWith("skipped", StringComparison.Ordinal))
                ?? run[0].Decision.Decider ?? "";
            var decisions = new CheckpointDecisions(checkpoint is { } named ? DecisionFabric.CheckpointWireName(named) : "", decider,
                [.. run.Select(pair => new QuestionDecision(pair.Decision.QuestionId, pair.Decision.Confidence, pair.Decision.Action,
                    catalogue.TryResolve(pair.Decision.QuestionId, out var question) && !string.Equals(pair.Decision.Action, question.BelowFloor, StringComparison.Ordinal)))]);
            output.Add((run[0].Item.Sequence, new DecisionsMade(decisions) { At = run[0].Item.RecordedAt }));
            firstInTurn = false;
            run.Clear();
        }

        long previous = -1;
        foreach (var item in stored.OrderBy(item => item.Sequence))
        {
            if (!visible(item.Sequence))
                continue;
            if (item.Payload is Decision decision)
            {
                // Two checkpoints back to back over one state (a step's, then egress's) part where no checkpoint asks both.
                var continues = run.Count > 0 && item.Sequence == previous + 1
                    && SameState(run[0].Decision.State, decision.State)
                    && run.All(pair => !string.Equals(pair.Decision.QuestionId, decision.QuestionId, StringComparison.Ordinal))
                    && Shared(run.Select(pair => pair.Decision.QuestionId).Append(decision.QuestionId), catalogue).Count > 0;
                if (!continues)
                    Flush();
                run.Add((item, decision));
            }
            else
            {
                Flush();
                if (item.Payload is UserMessage)
                    firstInTurn = true;
            }
            previous = item.Sequence;
        }
        Flush();
        return output;
    }

    /// <summary>The checkpoints every one of the questions is asked at; a question the catalogue lacks asks at none.</summary>
    private static List<Checkpoint> Shared(IEnumerable<string> questions, QuestionCatalogue catalogue)
    {
        IEnumerable<Checkpoint>? shared = null;
        foreach (var id in questions)
        {
            var at = catalogue.TryResolve(id, out var question) ? question.Checkpoints : [];
            shared = shared is null ? at : shared.Intersect(at);
        }
        return [.. shared ?? []];
    }

    private static bool SameState(JsonElement? first, JsonElement? second) =>
        (first, second) switch
        {
            (null, null) => true,
            ({ } a, { } b) => a.GetRawText() == b.GetRawText(),
            _ => false,
        };

    /// <summary>
    /// The compactions the conversation holds (a rewind may have taken one back, and the projection drops a malformed one),
    /// each with whether a later <c>CompactionExpanded</c> shows its range again.
    /// </summary>
    private static IEnumerable<(long Sequence, SessionStreamUpdate Update)> Compactions(IEnumerable<StoredEvent> stored, SessionSnapshot snapshot)
    {
        var issues = snapshot.Issues.Select(issue => issue.Sequence).ToHashSet();
        var visible = stored.Where(item => snapshot.IsVisible(item.Sequence) && !issues.Contains(item.Sequence)).OrderBy(item => item.Sequence).ToList();
        var expanded = visible.Select(item => item.Payload).OfType<CompactionExpanded>().Select(item => item.CompactedSequence).ToHashSet();
        return visible.Where(item => item.Payload is Compacted)
            .Select(item => (item.Sequence, (SessionStreamUpdate)new CompactionLogged((Compacted)item.Payload, item.Sequence)
            {
                At = item.RecordedAt,
                Expanded = expanded.Contains(item.Sequence),
            }))
            .ToList();
    }

    /// <summary>
    /// The scope of an answer logged before <c>ApprovalResolved.Scope</c> existed, from the pipeline's own words for it
    /// ("user: approved (session)"); null when nobody answered.
    /// </summary>
    private static string? ScopeOf(string reason)
    {
        if (!reason.StartsWith("user:", StringComparison.Ordinal))
            return null;
        foreach (var scope in (string[])["once", "session", "always"])
            if (reason.EndsWith($"({scope})", StringComparison.Ordinal))
                return scope;
        return null;
    }
}
