using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Kernel.Events;

namespace Corvid.Host.Sessions;

/// <summary>What a replay includes besides the conversation itself.</summary>
/// <param name="LedgerDiffs">Each finished file edit's diff from the write ledger, as a live turn reports it.</param>
/// <param name="Plans">The task list as it changed (<see cref="PlanChanged"/>).</param>
/// <param name="Checkpoints">The write ledger's checkpoints (<see cref="CheckpointLogged"/>).</param>
public sealed record ReplayOptions(bool LedgerDiffs = true, bool Plans = true, bool Checkpoints = true)
{
    /// <summary>Everything a live turn reports that the log holds.</summary>
    public static ReplayOptions Full { get; } = new();

    /// <summary>The conversation alone: messages, calls and their results as text (ACP's <c>session/load</c>).</summary>
    public static ReplayOptions Transcript { get; } = new(LedgerDiffs: false, Plans: false, Checkpoints: false);
}

/// <summary>A session read back from the log: its updates in order, and the log head they run to.</summary>
/// <param name="Head">The last position read: a live report from here on continues the replay exactly.</param>
public sealed record SessionHistory(string SessionId, long Head, ImmutableArray<SessionStreamUpdate> Updates);

/// <summary>
/// A stored session as the same updates a live turn reports (K-10): the conversation as it stands (compactions expanded,
/// undone turns left out), so a resumed session and a live one look alike on every surface. Decisions are not replayed:
/// the log keeps each answer, not the checkpoint that asked it.
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

        var items = new List<(long Sequence, SessionStreamUpdate Update)>();
        foreach (var entry in snapshot.Transcript)
        {
            var at = stored.TryGetValue(entry.FromSequence, out var item) ? item.RecordedAt : default;
            switch (entry.Content)
            {
                case UserMessage user:
                    items.Add((entry.FromSequence, new PromptSent(user.Text, entry.FromSequence) { At = at }));
                    break;
                case AssistantMessage assistant:
                    items.Add((entry.FromSequence, new AnswerChunk(assistant.Text) { At = at }));
                    break;
                case ToolCallIssued call:
                    var failed = results.TryGetValue(call.CallId, out var outcome) && outcome.IsError;
                    items.Add((entry.FromSequence, new ToolCallStarted(call.CallId, ToolPresentation.Title(call.ToolName, call.Arguments),
                        tools.Kind(call.ToolName), failed ? ToolCallState.Failed : ToolCallState.Completed)
                    {
                        At = at,
                        Tool = call.ToolName,
                        Input = call.Arguments,
                        Locations = tools.Locations(call.Arguments),
                    }));
                    break;
                case ToolResult result:
                    IReadOnlyList<FileDiff> diffs = [];
                    if (options.LedgerDiffs
                        && checkpoints.FirstOrDefault(c => c.Checkpoint is { Kind: CheckpointKinds.Before } b && b.CallId == result.CallId) is { } before
                        && checkpoints.FirstOrDefault(c => c.Checkpoint is { Kind: CheckpointKinds.After } a && a.CallId == result.CallId) is { Checkpoint.Paths: { Length: > 0 } paths } after)
                        diffs = await tools.LedgerDiffsAsync(before.Checkpoint.Revision, after.Checkpoint.Revision, paths).ConfigureAwait(false);
                    items.Add((entry.FromSequence, new ToolCallChanged(result.CallId)
                    {
                        At = at,
                        Tool = calls.GetValueOrDefault(result.CallId)?.ToolName,
                        Kind = calls.GetValueOrDefault(result.CallId) is { } issued ? tools.Kind(issued.ToolName) : null,
                        Status = result.IsError ? ToolCallState.Failed : ToolCallState.Completed,
                        Output = result.Result,
                        OutputText = ToolPresentation.ResultText(result.Result),
                        Diffs = [.. diffs],
                    }));
                    break;
            }
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
        var ordered = options.Plans || options.Checkpoints ? items.OrderBy(item => item.Sequence).ToList() : items;
        return new SessionHistory(sessionId, head, [.. ordered.Select(item => item.Update)]);
    }
}
