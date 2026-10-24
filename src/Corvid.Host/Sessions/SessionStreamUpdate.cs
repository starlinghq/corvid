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

/// <summary>Answer text (ACP <c>agent_message_chunk</c>): a streamed delta live, the whole message on replay.</summary>
public sealed record AnswerChunk(string Text) : SessionStreamUpdate;

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
}

/// <summary>The model's task list, replaced whole (ACP <c>plan</c>), from a <c>TodosSet</c> event.</summary>
public sealed record PlanChanged(ImmutableArray<TodoItem> Items) : SessionStreamUpdate;

/// <summary>A checkpoint's decisions, from the feed (live only: the log has each answer but not its checkpoint).</summary>
public sealed record DecisionsMade(CheckpointDecisions Decisions) : SessionStreamUpdate;

/// <summary>A write-ledger checkpoint was recorded (<c>CheckpointTaken</c>), for a rewind timeline.</summary>
public sealed record CheckpointLogged(CheckpointTaken Checkpoint, long Sequence) : SessionStreamUpdate;
