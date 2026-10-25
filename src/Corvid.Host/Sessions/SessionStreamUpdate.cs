using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Telemetry;

namespace Corvid.Host.Sessions;

/// <summary>
/// One update in a session's stream (K-10, docs/plan/tui.md §3.2): what a surface shows of a session, the same whether it
/// comes live from a running turn (<see cref="SessionUpdateReporter"/>, over the <see cref="TurnFeed"/> and the log) or
/// from the log of a stored one (<see cref="SessionReplay"/>). The records are shaped like ACP's <c>session/update</c>
/// notifications and carry no surface's types: <c>corvid acp</c> maps them onto ACP, the windowed app onto its own update
/// contract.
/// </summary>
public abstract record SessionStreamUpdate
{
    /// <summary>When it happened: the event's record time when it comes from the log, the report's time otherwise.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>The user's message (ACP <c>user_message_chunk</c>, whole), from its <c>UserMessage</c> event.</summary>
/// <param name="Sequence">The event's position in the log: a fork point.</param>
public sealed record PromptSent(string Text, long Sequence) : SessionStreamUpdate;

/// <summary>
/// A message the person steered into a running turn (K-3), from its <c>UserSteered</c> event: part of the turn it was taken
/// by, not the start of one.
/// </summary>
/// <param name="Sequence">The event's position in the log.</param>
public sealed record SteerSent(string Text, long Sequence) : SessionStreamUpdate;

/// <summary>
/// The clarifying question a turn ended on (K-6), from its <c>ClarificationAsked</c> event (after the answer that carries
/// the same text). Model text: untrusted. The person's next message answers it.
/// </summary>
/// <param name="Options">Answers to pick from; empty for free text.</param>
public sealed record QuestionRaised(string QuestionId, string Text, ImmutableArray<string> Options) : SessionStreamUpdate;

/// <summary>
/// The running turn began a step (a generator response with tool calls), live from the turn loop; on replay from the step
/// its calls record, with no budget.
/// </summary>
/// <param name="MaxSteps">The turn's step budget; null on replay.</param>
public sealed record TurnStepped(int Step, int? MaxSteps) : SessionStreamUpdate;

/// <summary>Answer text (ACP <c>agent_message_chunk</c>): a streamed delta live, the whole message on replay.</summary>
public sealed record AnswerChunk(string Text) : SessionStreamUpdate;

/// <summary>
/// Reasoning the generator streamed (ACP <c>agent_thought_chunk</c>, K-1): live only, a delta; the log does not keep it,
/// so a replay has none.
/// </summary>
public sealed record ThoughtChunk(string Text) : SessionStreamUpdate;

/// <summary>
/// One generator call's tokens as its provider reported them (K-5, live only: the log does not keep usage yet). The
/// prompt's count is the rendered prompt the call sent, which is what a context gauge shows.
/// </summary>
public sealed record UsageReported(long? InputTokens, long? OutputTokens) : SessionStreamUpdate;

/// <summary>A tool call's state, as ACP names it.</summary>
public enum ToolCallState
{
    /// <summary>Announced, not running: waiting for its approval.</summary>
    Pending,
    InProgress,
    Completed,
    Failed,
}

/// <summary>
/// A tool call became known (ACP <c>tool_call</c>): live when the kernel starts it (titled by the tool's name, its input
/// not read yet) or when an approval prompt names it; on replay, finished, with its input.
/// </summary>
/// <param name="Kind">One of <see cref="ToolCallKinds"/>.</param>
public sealed record ToolCallStarted(string CallId, string Title, string Kind, ToolCallState Status) : SessionStreamUpdate
{
    /// <summary>The tool's name, when known (not for a prompt outside any call).</summary>
    public string? Tool { get; init; }

    /// <summary>The call's arguments as logged (credential-scrubbed, C-040).</summary>
    public JsonElement? Input { get; init; }

    /// <summary>Files it names, as absolute paths the tools may open.</summary>
    public ImmutableArray<string> Locations { get; init; } = [];
}

/// <summary>A tool call changed (ACP <c>tool_call_update</c>): it finished, with what it did. Null or empty leaves a part as it was.</summary>
public sealed record ToolCallChanged(string CallId) : SessionStreamUpdate
{
    public string? Title { get; init; }

    public string? Tool { get; init; }

    /// <summary>One of <see cref="ToolCallKinds"/>, when <see cref="Tool"/> is known.</summary>
    public string? Kind { get; init; }

    public ToolCallState? Status { get; init; }

    /// <summary>
    /// How it ended, as the kernel's <c>execute_tool</c> span says: <c>succeeded</c>, <c>failed</c>,
    /// <c>invalid_arguments</c>, <c>denied</c> or <c>cancelled</c>. Live only; a replayed call has only <see cref="Status"/>.
    /// </summary>
    public string? Outcome { get; init; }

    public JsonElement? Input { get; init; }

    /// <summary>The result as logged.</summary>
    public JsonElement? Output { get; init; }

    /// <summary>The result as text, cut to a readable size (<see cref="ToolPresentation.ResultText"/>).</summary>
    public string? OutputText { get; init; }

    public ImmutableArray<string> Locations { get; init; } = [];

    /// <summary>What it changed, from the write ledger's checkpoints around it (C-014).</summary>
    public ImmutableArray<FileDiff> Diffs { get; init; } = [];

    /// <summary>
    /// K-2, live only: the running call's output so far as its last lines, replacing the previous tail (bounded and
    /// credential-scrubbed by the kernel; untrusted). An update with a tail carries nothing else but the call's state.
    /// </summary>
    public string? Tail { get; init; }

    public TimeSpan? Elapsed { get; init; }

    /// <summary>
    /// How the call's approval prompt was answered, from the log (replay only, with <see cref="ReplayOptions.Approvals"/>);
    /// null when nobody was asked. Live, the surface's own prompter knows the answer.
    /// </summary>
    public CallApproval? Approval { get; init; }

    /// <summary>
    /// Why the call ran, or was refused, without anyone being asked (G-2): the approval pipeline's assessment as the log
    /// records it, live and on replay (with <see cref="ReplayOptions.Approvals"/>); null when someone was asked or the call
    /// never reached the pipeline (a tool that declares itself read-only: see <see cref="ReadOnly"/>).
    /// </summary>
    public ApprovalAssessed? Assessment { get; init; }

    /// <summary>
    /// The tool declared itself read-only when it was called (<c>ToolCallIssued.ReadOnly</c>): with no
    /// <see cref="Assessment"/> and no <see cref="Approval"/>, it ran unasked because it only reads. Null when unknown, and
    /// for a call that was asked all the same (a network read in a tainted turn).
    /// </summary>
    public bool? ReadOnly { get; init; }

    /// <summary>The write ledger's checkpoint taken before the call, which makes what it changed reversible (C-014).</summary>
    public string? CheckpointBefore { get; init; }
}

/// <summary>A call's approval prompt and its answer as the log records them (<c>ApprovalRequested</c>, <c>ApprovalResolved</c>).</summary>
/// <param name="Reason">The pipeline's words for the resolution: "user: denied (once)", "timeout: …", "cancelled".</param>
public sealed record CallApproval(string ApprovalId, bool Approved, string Reason)
{
    /// <summary>The person answered (approved or denied), rather than a deadline, a cancellation or a failed prompt.</summary>
    public bool Answered => Reason.StartsWith("user:", StringComparison.Ordinal);

    /// <summary>The answer's scope as applied: <c>once</c>, <c>session</c> or <c>always</c>; null when nobody answered.</summary>
    public string? Scope { get; init; }

    /// <summary>The reason the person gave with a denial.</summary>
    public string? Note { get; init; }
}

/// <summary>The model's task list, replaced whole (ACP <c>plan</c>), from a <c>TodosSet</c> event.</summary>
public sealed record PlanChanged(ImmutableArray<TodoItem> Items) : SessionStreamUpdate;

/// <summary>A checkpoint's decisions, from the feed (live only: the log has each answer but not its checkpoint).</summary>
public sealed record DecisionsMade(CheckpointDecisions Decisions) : SessionStreamUpdate;

/// <summary>A write-ledger checkpoint was recorded (<c>CheckpointTaken</c>), for a rewind timeline.</summary>
public sealed record CheckpointLogged(CheckpointTaken Checkpoint, long Sequence) : SessionStreamUpdate;
