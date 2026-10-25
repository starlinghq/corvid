using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Kernel.Events;

namespace Corvid.Host.Sessions;

/// <summary>What a replay includes besides the conversation itself.</summary>
/// <param name="LedgerDiffs">Each finished file edit's diff from the write ledger, as a live turn reports it.</param>
/// <param name="Plans">The task list as it changed (<see cref="PlanChanged"/>).</param>
/// <param name="Checkpoints">The write ledger's checkpoints (<see cref="CheckpointLogged"/>).</param>
/// <param name="Approvals">
/// Each asked call's approval answer (<see cref="ToolCallChanged.Approval"/>), and a refused call's outcome as
/// <c>denied</c>, as a live turn shows it.
/// </param>
/// <param name="UnfinishedCalls">
/// A call the log holds no result for (its turn was interrupted, or the process stopped under it) replays as failed and
/// cancelled, as the live turn closed it, rather than as completed.
/// </param>
public sealed record ReplayOptions(bool LedgerDiffs = true, bool Plans = true, bool Checkpoints = true, bool Approvals = true,
    bool UnfinishedCalls = true)
{
    /// <summary>Everything a live turn reports that the log holds.</summary>
    public static ReplayOptions Full { get; } = new();

    /// <summary>
    /// The conversation alone: messages, calls and their results as text (ACP's <c>session/load</c>, whose recorded
    /// exchanges keep a call without a result as completed).
    /// </summary>
    public static ReplayOptions Transcript { get; } = new(LedgerDiffs: false, Plans: false, Checkpoints: false, Approvals: false, UnfinishedCalls: false);
}

/// <summary>A session read back from the log: its updates in order, and the log head they run to.</summary>
/// <param name="Head">The last position read: a live report from here on continues the replay exactly.</param>
public sealed record SessionHistory(string SessionId, long Head, ImmutableArray<SessionStreamUpdate> Updates);

/// <summary>
/// A stored session as the same updates a live turn reports (K-10): the conversation as it stands (compactions expanded,
/// undone turns left out), so a resumed session and a live one look alike on every surface. Decisions are not replayed:
/// the log keeps each answer, not the checkpoint that asked it. An asked call carries its approval's answer. The log has
/// no record of how a turn ended (an interrupt) or of the session's taint, so a replay shows neither.
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
        var head = through ?? runtime.Log.GetHead(sessionId, cancellationToken);
        var snapshot = SessionProjection.Load(runtime.Log, sessionId, head, expandCompactions: true, cancellationToken);
        // Each transcript entry's time is its event's: read once, by position, with the plans and checkpoints between them.
        var stored = SessionProjection.ReadRange(runtime.Log, sessionId, 0, head, cancellationToken).ToDictionary(item => item.Sequence);
        var results = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolResult>()
            .GroupBy(result => result.CallId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var calls = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolCallIssued>()
            .GroupBy(call => call.CallId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var checkpoints = snapshot.CheckpointEvents.Where(item => snapshot.IsVisible(item.Sequence)).ToList();
        var approvals = options.Approvals ? Approvals(stored.Values, snapshot.IsVisible) : [];

        var items = new List<(long Sequence, SessionStreamUpdate Update)>();
        int? round = null;
        foreach (var entry in snapshot.Transcript)
        {
            var at = stored.TryGetValue(entry.FromSequence, out var item) ? item.RecordedAt : default;
            switch (entry.Content)
            {
                case UserMessage user:
                    items.Add((entry.FromSequence, new PromptSent(user.Text, entry.FromSequence) { At = at }));
                    round = null;
                    break;
                // K-3: a steer is part of the turn that took it.
                case UserSteered steer:
                    items.Add((entry.FromSequence, new SteerSent(steer.Text, entry.FromSequence) { At = at }));
                    break;
                case AssistantMessage assistant:
                    items.Add((entry.FromSequence, new AnswerChunk(assistant.Text) { At = at }));
                    break;
                case ToolCallIssued call:
                    // The step its round records, as the live turn reported it when the step began (with no budget: the log has none).
                    if (call.Round is { } step and > 0 && step != round)
                    {
                        round = step;
                        items.Add((entry.FromSequence, new TurnStepped(step, null) { At = at }));
                    }
                    var finished = results.TryGetValue(call.CallId, out var outcome);
                    var unfinished = !finished && options.UnfinishedCalls;
                    var failed = (finished && outcome!.IsError) || unfinished;
                    items.Add((entry.FromSequence, new ToolCallStarted(call.CallId, ToolPresentation.Title(call.ToolName, call.Arguments),
                        tools.Kind(call.ToolName), failed ? ToolCallState.Failed : ToolCallState.Completed)
                    {
                        At = at,
                        Tool = call.ToolName,
                        Input = call.Arguments,
                        Locations = tools.Locations(call.Arguments),
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
                    items.Add((entry.FromSequence, new ToolCallChanged(result.CallId)
                    {
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
                    }));
                    break;
            }
        }

        // K-6: each clarifying question after the answer that carries it, as the live turn reported it.
        var questions = false;
        foreach (var item in stored.Values.Where(item => item.Payload is ClarificationAsked && snapshot.IsVisible(item.Sequence)))
        {
            questions = true;
            var question = (ClarificationAsked)item.Payload;
            items.Add((item.Sequence, new QuestionRaised(question.QuestionId, question.Text, question.Options.IsDefault ? [] : question.Options)
            {
                At = item.RecordedAt,
            }));
        }
        if (options.Plans)
            foreach (var item in stored.Values.Where(item => item.Payload is TodosSet && snapshot.IsVisible(item.Sequence)))
                items.Add((item.Sequence, new PlanChanged(((TodosSet)item.Payload).Items) { At = item.RecordedAt }));
        if (options.Checkpoints)
            foreach (var checkpoint in checkpoints)
                items.Add((checkpoint.Sequence, new CheckpointLogged(checkpoint.Checkpoint, checkpoint.Sequence)
                {
                    At = stored.TryGetValue(checkpoint.Sequence, out var item) ? item.RecordedAt : default,
                }));

        // The transcript in its own order; plans and checkpoints placed among it by position (a stable sort, so the
        // transcript's order holds for entries at one position).
        var ordered = options.Plans || options.Checkpoints || questions ? items.OrderBy(item => item.Sequence).ToList() : items;
        return new SessionHistory(sessionId, head, [.. ordered.Select(item => item.Update)]);
    }

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
