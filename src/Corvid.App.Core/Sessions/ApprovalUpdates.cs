using System.Collections.Immutable;

namespace Corvid.App.Core.Sessions;

/// <summary>How long an answer lasts: the pipeline's <c>ApprovalScope</c>.</summary>
public enum ApprovalScope
{
    Once,
    Session,
    Always,
}

/// <summary>
/// Something a parsed command touches, as the approval card draws it: "<c>./build/</c> · workspace · 212 files".
/// </summary>
/// <param name="Value">The target as written or resolved (a path, host, URL, process).</param>
/// <param name="Access">The pipeline's access name: read, write, delete, execute, signal, connect or send.</param>
public sealed record CommandTarget(string Value, string Access)
{
    /// <summary>path, process, process_name, host, url or recipient.</summary>
    public string Kind { get; init; } = "path";

    /// <summary>For a path: inside, workspace_root, outside or unresolved.</summary>
    public string? Location { get; init; }

    /// <summary>What the engine adds ("212 files").</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// A target the command reaches by convention rather than by naming it, such as the build outputs of <c>dotnet test</c>
    /// ("writes under <c>bin/ obj/ TestResults/</c>").
    /// </summary>
    public bool Implied { get; init; }
}

/// <summary>A labelled line under a parsed command: "undo", "network". Text in backticks is drawn as code.</summary>
public sealed record CommandNote(string Label, string Text);

/// <summary>
/// One command of the action after the pipeline unwrapped it (<c>AnalyzedCommand</c>): its text, verb, subcommand,
/// flags, effects and targets.
/// </summary>
public sealed record ParsedCommand(string Text, string Verb)
{
    public string? Subcommand { get; init; }

    public ImmutableArray<string> Flags { get; init; } = [];

    /// <summary>The pipeline's effect names: read, write, delete, recursive, execute, network, signal, elevated, system…</summary>
    public ImmutableArray<string> Effects { get; init; } = [];

    public ImmutableArray<CommandTarget> Targets { get; init; } = [];

    /// <summary>Wrappers it was unwrapped from, outermost first (<c>timeout</c>, <c>sh -c</c>, <c>sudo</c>).</summary>
    public ImmutableArray<string> Via { get; init; } = [];

    /// <summary>Why part of it is unknown before it runs; anything here means it is never allowed unasked.</summary>
    public ImmutableArray<string> Unresolved { get; init; } = [];

    /// <summary>What happens around it: whether a checkpoint makes it undoable, what network the sandbox gives it.</summary>
    public ImmutableArray<CommandNote> Notes { get; init; } = [];
}

/// <summary>
/// Why the pipeline asks, or a fact the user needs to answer: a reason with its code (<c>ApprovalReasons</c>, a hardline
/// rule, <c>risk</c>) or a context row such as taint and reversibility.
/// </summary>
/// <param name="Code">The machine code: <c>risk</c>, <c>tainted_network_egress</c>, <c>credential_read</c>, <c>detection</c>…</param>
/// <param name="Label">The short code the card shows beside it ("risk 3", "taint", "reversible").</param>
/// <param name="Detail">The sentence; text in backticks is drawn as code.</param>
public sealed record ApprovalReason(string Code, string Label, string Detail)
{
    /// <summary>A shorter sentence for the phone's card, when the engine has one.</summary>
    public string? Short { get; init; }
}

/// <summary>
/// A structural rule the answer would write (docs/plan/ui.md §4.4): "allow <c>rm -r</c> under paths build/". Shown before
/// "always" is confirmed. Narrowing <see cref="Paths"/> before saving needs G-11; listing and removing rules K-7.
/// </summary>
/// <param name="Decision">allow or deny.</param>
/// <param name="Command">The verb and subcommand or flags the rule matches ("rm -r", "dotnet test").</param>
public sealed record RulePreview(string Decision, string Command)
{
    /// <summary>Paths the rule covers; editable, and only narrower, before it is written.</summary>
    public ImmutableArray<string> Paths { get; init; } = [];

    /// <summary>The rest of its scope in words ("in the workspace, no network, no elevation").</summary>
    public string? Scope { get; init; }
}

/// <summary>A logged event a detection rule matched, by its sequence and a line saying what it was.</summary>
public sealed record DetectionEvent(long Sequence, string Summary);

/// <summary>A detection rule's match (<c>DetectionMatched</c>, C-023).</summary>
/// <param name="Posture"><c>enforce</c> forces an ask; <c>detect</c> only records.</param>
public sealed record DetectionInfo(string RuleId, string Severity, string Posture, string Reason)
{
    public ImmutableArray<DetectionEvent> Events { get; init; } = [];

    /// <summary>The match forced an approval prompt.</summary>
    public bool Enforced { get; init; }

    /// <summary>The pending call that completed the match.</summary>
    public string? CallId { get; init; }
}

/// <summary>An MCP server's trust, for a card about one of its tools.</summary>
public sealed record McpTrust(string Server, string Trust);

/// <summary>
/// The approval pipeline asks the user (ACP <c>session/request_permission</c>, carried as an update with everything the
/// card shows: docs/plan/ui.md §4.4). <see cref="ApprovalResolved"/> closes it. Several can wait at once; they are asked
/// in order ("1 of 2").
/// </summary>
/// <param name="Tool">The tool waiting on it ("shell").</param>
/// <param name="Text">The action as the pipeline's analysis states it: the command line, or the tool and its target.</param>
/// <param name="Deadline">When an unanswered prompt is denied (five minutes by default).</param>
public sealed record ApprovalRequested(string ApprovalId, string Tool, string Text, DateTimeOffset Deadline) : SessionUpdate
{
    /// <summary>The tool call waiting on the prompt; null for an approval outside a call (a memory write).</summary>
    public string? CallId { get; init; }

    public ImmutableArray<ParsedCommand> Commands { get; init; } = [];

    public ImmutableArray<ApprovalReason> Reasons { get; init; } = [];

    /// <summary>Exactly the scopes the pipeline offers (<c>ApprovalPrompt.Offered</c>); deny is always possible.</summary>
    public ImmutableArray<ApprovalScope> Offered { get; init; } = [ApprovalScope.Once];

    /// <summary>For a command outside the parsed shell subset: the parser's reason.</summary>
    public string? Unparsed { get; init; }

    /// <summary>Whether a checkpoint taken first makes the call reversible; null when unknown.</summary>
    public bool? Reversible { get; init; }

    /// <summary>The checkpoint taken before the call.</summary>
    public string? CheckpointId { get; init; }

    /// <summary>What tainted the session ("web_fetch docs.example.org"), when it is tainted.</summary>
    public string? TaintSource { get; init; }

    /// <summary>The edit a <c>write_file</c> or <c>patch_file</c> call would make.</summary>
    public FileEdit? ProposedDiff { get; init; }

    /// <summary>For an MCP tool, its server's trust.</summary>
    public McpTrust? Mcp { get; init; }

    /// <summary>The rules a session or always answer would write, one per side-effecting command.</summary>
    public ImmutableArray<RulePreview> Rules { get; init; } = [];

    /// <summary>Detection matches that forced this ask, with the events they matched.</summary>
    public ImmutableArray<DetectionInfo> Detections { get; init; } = [];

    /// <summary>
    /// A5: for a <c>delegate_task</c> spawn, each child's plan (goal, toolset and what was left out, budget, model mix,
    /// approval mode); for a child's own call, which child asks. Lines as the host's card has them, hidden characters
    /// already shown as escapes.
    /// </summary>
    public ImmutableArray<string> Delegation { get; init; } = [];

    /// <summary>
    /// A5 review (security L5): <see cref="Delegation"/> with every goal in full, when a goal is longer than a card line
    /// shows (the cut line says how many characters it leaves out); empty when nothing was cut.
    /// </summary>
    public ImmutableArray<string> DelegationWhole { get; init; } = [];
}

/// <summary>How a prompt closed.</summary>
public enum ApprovalOutcome
{
    Approved,
    Denied,
    /// <summary>Nobody answered before the deadline: denied ("denied: deadline").</summary>
    TimedOut,
    /// <summary>The turn or a rewind cancelled it: denied, and nothing runs on a stale prompt.</summary>
    Cancelled,
}

/// <summary>A prompt closed (<c>ApprovalResolved</c>); its card collapses into the tool card's verdict.</summary>
public sealed record ApprovalResolved(string ApprovalId, ApprovalOutcome Outcome) : SessionUpdate
{
    public ApprovalScope Scope { get; init; } = ApprovalScope.Once;

    /// <summary>The rule a session or always answer wrote, in words ("`dotnet test` in workspace").</summary>
    public string? Rule { get; init; }

    /// <summary>The user's reason for a denial, which is also sent as the next message.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// A detection rule matched (<c>DetectionMatched</c>): a red row with the rule, its severity and posture and the events,
/// linked to Audit (docs/plan/ui.md F10).
/// </summary>
public sealed record DetectionMatched(DetectionInfo Detection) : SessionUpdate;
