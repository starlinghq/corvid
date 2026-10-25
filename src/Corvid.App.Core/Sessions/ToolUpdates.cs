using System.Collections.Immutable;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// A tool call's kind, which picks its card's icon: ACP's kinds as <c>ToolPresentation.Kind</c> assigns them, plus the
/// two the TUI design adds (memory, MCP).
/// </summary>
public enum ToolKind
{
    Read,
    Edit,
    Delete,
    Search,
    Execute,
    Fetch,
    Memory,
    Mcp,
    Other,
}

/// <summary>ACP's tool call statuses.</summary>
public enum ToolCallStatus
{
    /// <summary>Announced, not running yet: waiting for its approval or its turn.</summary>
    Pending,
    InProgress,
    Completed,
    Failed,
}

/// <summary>How a call ended, as the kernel's <c>execute_tool</c> span records it.</summary>
public enum ToolOutcome
{
    Succeeded,
    Failed,
    InvalidArguments,
    Denied,
    Cancelled,
}

/// <summary>A file edit (ACP <c>diff</c> content): the whole file before and after, from the write ledger.</summary>
/// <param name="OldText">The file before the call; null for a new file.</param>
public sealed record FileEdit(string Path, string? OldText, string NewText);

/// <summary>Why a call was allowed to run, or was not: the approval pipeline's resolution, in the card's words.</summary>
public enum VerdictKind
{
    /// <summary>It only reads ("auto: read-only").</summary>
    ReadOnly,
    /// <summary>The mode and the <c>risk</c> answer let it run unasked ("smart mode, risk 1 (0.93)").</summary>
    Mode,
    /// <summary>A session rule matched.</summary>
    SessionRule,
    /// <summary>An always rule matched.</summary>
    AlwaysRule,
    ApprovedOnce,
    ApprovedSession,
    ApprovedAlways,
    /// <summary>The user denied it.</summary>
    Denied,
    /// <summary>A remembered denial matched.</summary>
    RuleDenied,
    /// <summary>The hardline list refused it, whatever the mode.</summary>
    Hardline,
    /// <summary>Nobody answered before the deadline.</summary>
    TimedOut,
    /// <summary>The turn was cancelled while the prompt was open.</summary>
    Cancelled,
}

/// <summary>
/// The approval verdict a card carries, so a side-effecting call says why it ran even when nobody was asked ("ran under
/// a session rule: `dotnet test` in workspace (approval a-31, turn 10) · risk 1 (0.93) · checkpoint c-44 taken before the
/// call"). For calls that ran unasked this needs G-2: the pipeline's assessment on the stream. Text in backticks is
/// drawn as code.
/// </summary>
public sealed record CallVerdict(VerdictKind Kind)
{
    /// <summary>The approval that created the rule, or that answered this call.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>The turn whose approval wrote the matching rule.</summary>
    public int? RuleTurn { get; init; }

    /// <summary>The matching rule in words ("`dotnet test` in workspace").</summary>
    public string? Rule { get; init; }

    /// <summary>The <c>risk</c> level the decider gave (0 to 4), when it was asked.</summary>
    public int? RiskLevel { get; init; }

    public double? RiskConfidence { get; init; }

    /// <summary>The mode the call ran under, for <see cref="VerdictKind.Mode"/>.</summary>
    public Shell.ApprovalMode? Mode { get; init; }

    /// <summary>The checkpoint taken before the call, which makes it reversible.</summary>
    public string? CheckpointId { get; init; }

    /// <summary>What else the pipeline said: a hardline rule, a denial's reason.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// A tool call started or was announced (ACP <c>tool_call</c>). <see cref="ToolCallUpdate"/>s with the same id change it.
/// </summary>
/// <param name="Tool">The tool's name ("read_file").</param>
public sealed record ToolCall(string CallId, string Tool, ToolKind Kind, ToolCallStatus Status) : SessionUpdate
{
    /// <summary>What it acts on, drawn in monospace after the tool's name: its command line, path, URL or query.</summary>
    public string? Target { get; init; }

    /// <summary>The raw input as JSON, as logged (credential-scrubbed, C-040).</summary>
    public string? Input { get; init; }

    /// <summary>Files it names, as workspace paths.</summary>
    public ImmutableArray<string> Locations { get; init; } = [];

    /// <summary>Why it may run (G-2 when nobody was asked).</summary>
    public CallVerdict? Verdict { get; init; }
}

/// <summary>Changes to a tool call (ACP <c>tool_call_update</c>); a null or empty member leaves that part as it was.</summary>
public sealed record ToolCallUpdate(string CallId) : SessionUpdate
{
    public ToolCallStatus? Status { get; init; }

    /// <summary>How it ended, with <see cref="Status"/> completed or failed.</summary>
    public ToolOutcome? Outcome { get; init; }

    public string? Target { get; init; }

    /// <summary>A few words on the result, drawn after the target ("3 hits", "48 KB").</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// The last lines of output while the call runs, replacing the previous tail (K-2: the shell's output and web_fetch's
    /// progress, bounded and credential-scrubbed by the kernel, reported at most a few times a second). Live only: the
    /// log keeps the result, not the tail. Untrusted text.
    /// </summary>
    public string? Tail { get; init; }

    /// <summary>The result as text, for the expanded card and the Output panel (cut to a readable size by the engine).</summary>
    public string? Output { get; init; }

    public TimeSpan? Elapsed { get; init; }

    /// <summary>File edits from the write ledger, for <c>write_file</c> and <c>patch_file</c>.</summary>
    public ImmutableArray<FileEdit> Diffs { get; init; } = [];

    public ImmutableArray<string> Locations { get; init; } = [];

    public CallVerdict? Verdict { get; init; }
}
