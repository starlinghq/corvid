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
public sealed record PromptSent(string Text, long Sequence) : SessionStreamUpdate
{
    /// <summary>
    /// The generator the turn ran on, as its first decision's TurnState names it ("qwen3.5-0.8b"): replay only, with
    /// <see cref="ReplayOptions.Generators"/>; null live (the surface knows its slot) and when the log has no decision for it.
    /// </summary>
    public string? Generator { get; init; }
}

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

    public TimeSpan? Elapsed { get; init; }

    /// <summary>
    /// How the call's approval prompt was answered, from the log (replay only, with <see cref="ReplayOptions.Approvals"/>);
    /// null when nobody was asked. Live, the surface's own prompter knows the answer.
    /// </summary>
    public CallApproval? Approval { get; init; }
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

/// <summary>
/// A checkpoint's decisions: live from the feed; on replay (<see cref="ReplayOptions.Decisions"/>) from the
/// <c>Decision</c> events one checkpoint appended together, its name read from the catalogue's questions.
/// </summary>
public sealed record DecisionsMade(CheckpointDecisions Decisions) : SessionStreamUpdate;

/// <summary>
/// Outside content entered the conversation, or the user's next message cleared it (plan §10.3): replay only
/// (<see cref="ReplayOptions.Taint"/>), from a network tool's result in the log and the next user message. Live, a surface
/// reads the session's own taint as the turn goes. The log does not record taint from an untrusted description or a hub
/// skill, so a replay shows the network reads only.
/// </summary>
/// <param name="Source">What brought it in: the tool and what it read ("web_fetch https://example.org").</param>
public sealed record TaintNoted(bool Tainted, string? Source) : SessionStreamUpdate;

/// <summary>
/// A compaction recorded in the log (<c>Compacted</c>), replay only (<see cref="ReplayOptions.Compactions"/>): live, the
/// surface that compacted reports its own result. The replay shows the conversation expanded, so the marker heads its
/// record over the range it summarized.
/// </summary>
/// <param name="Sequence">The event's position; <see cref="Compacted.FromSequence"/> and <c>ThroughSequence</c> the range.</param>
public sealed record CompactionLogged(Compacted Compaction, long Sequence) : SessionStreamUpdate
{
    /// <summary>A later <c>CompactionExpanded</c> shows the range expanded again.</summary>
    public bool Expanded { get; init; }
}

/// <summary>A write-ledger checkpoint was recorded (<c>CheckpointTaken</c>), for a rewind timeline.</summary>
public sealed record CheckpointLogged(CheckpointTaken Checkpoint, long Sequence) : SessionStreamUpdate;
