using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Tools;

namespace Corvid.Approvals;

/// <summary>What an action can change or reach, after unwrapping. A command with only <see cref="Read"/> is read-only.</summary>
[Flags]
public enum ActionEffects
{
    None = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    /// <summary>A write or delete that covers a directory tree.</summary>
    Recursive = 8,
    /// <summary>Runs code Corvid cannot see: an interpreter, a script file, a build, an unknown program.</summary>
    Execute = 16,
    Network = 32,
    /// <summary>Signals other processes.</summary>
    Signal = 64,
    /// <summary>Runs as another user (<c>sudo</c>, <c>doas</c>, <c>su</c>).</summary>
    Elevated = 128,
    /// <summary>Changes the machine: services, packages, mounts, users, power.</summary>
    System = 256,
    Configuration = 512,
    Messaging = 1024,
    Schedule = 2048,
    Memory = 4096,
    /// <summary>State held by another service or another machine.</summary>
    Remote = 8192,
}

public enum TargetKind
{
    Path,
    /// <summary>A numeric PID or process group (<c>kill -9 1234</c>, <c>kill -- -1</c>).</summary>
    Process,
    /// <summary>A process name or pattern (<c>pkill</c>, <c>killall</c>).</summary>
    ProcessName,
    Host,
    Url,
    /// <summary>Who a messaging tool sends to, as the caller resolved them.</summary>
    Recipient,
}

public enum TargetAccess
{
    Read,
    Write,
    Delete,
    Execute,
    Signal,
    Connect,
    Send,
}

/// <summary>Where a path target lies relative to the workspace, by lexical resolution (symlinks are the sandbox's concern).</summary>
public enum PathLocation
{
    Inside,
    /// <summary>The workspace directory itself.</summary>
    WorkspaceRoot,
    Outside,
    /// <summary>Not known statically: a variable, a substitution, an unknown working directory, or a pattern that spans both.</summary>
    Unresolved,
}

/// <summary>Something an action reads, writes, deletes, signals or connects to.</summary>
/// <param name="Value">
/// For a path, the absolute, normalized path; for a pattern, the normalized pattern (its directory is static); for a dynamic
/// path, the static prefix's directory, or empty. For a process, the PID; for a host, the host name; for a URL, the URL.
/// </param>
/// <param name="Location">Set for paths only.</param>
/// <param name="Recursive">The operation covers the whole tree under the path.</param>
/// <param name="Pattern">A glob: the path stands for every match.</param>
/// <param name="Dynamic">Part of the value is only known at run time.</param>
/// <param name="Written">The argument as written, for display.</param>
public sealed record ActionTarget(
    TargetKind Kind,
    string Value,
    TargetAccess Access,
    PathLocation? Location = null,
    bool Recursive = false,
    bool Pattern = false,
    bool Dynamic = false,
    string? Written = null);

/// <summary>An action a tool call proposes. Approval applies to every side-effecting tool, not only the shell (plan §10.1).</summary>
public abstract record ProposedAction;

/// <summary>A shell command line, run by a shell tool.</summary>
public sealed record ShellAction(string Command) : ProposedAction;

/// <summary>
/// A7: a script a code tool runs (<see cref="ApprovalOptions.CodeTools"/>), assessed as code of its own kind, never as a
/// shell command or a tool call whose arguments name paths: Corvid cannot know its effects before it runs, so it always
/// asks, once (its tool calls are each assessed as they are made).
/// </summary>
/// <param name="Tool">The code tool's name.</param>
/// <param name="Language">The script's language, as the call names it.</param>
/// <param name="Code">The script's source.</param>
public sealed record CodeAction(string Tool, string Language, string Code) : ProposedAction;

/// <summary>A call to a tool with C-005 risk metadata.</summary>
/// <param name="Targets">
/// Paths, hosts or URLs the caller extracted from the arguments. When default, string arguments whose parameter names say
/// path, file, directory, URL or host are used.
/// </param>
/// <param name="Recipients">Who a messaging tool sends to, as the caller resolved them; default means unknown.</param>
public sealed record ToolAction(
    ToolDescriptor Tool,
    JsonElement Arguments,
    ImmutableArray<ActionTarget> Targets = default,
    ImmutableArray<string> Recipients = default) : ProposedAction
{
    /// <summary>
    /// A5 review (security L5): the tools the calling turn was offered, when the kernel knows them, so a card that previews
    /// what the call would start (a <c>delegate_task</c> spawn's children) shows the toolset the call will get. Null: unknown.
    /// </summary>
    public IReadOnlySet<string>? OfferedTools { get; init; }
}

/// <summary>One command after unwrapping, with what it does and to what.</summary>
/// <param name="Verb">The program's name (<c>rm</c>), the tool name, or <c>?</c> when it is computed at run time.</param>
/// <param name="Subcommand">For programs that dispatch on one (<c>git push</c>, <c>systemctl stop</c>).</param>
/// <param name="Unresolved">Why part of the command is unknown statically. Any entry means it is never auto-approved.</param>
/// <param name="Via">Wrappers it was unwrapped from, outermost first (<c>timeout</c>, <c>sh -c</c>, <c>sudo</c>).</param>
/// <param name="Substitution">Runs inside a <c>$(...)</c> or backquote substitution.</param>
/// <param name="Text">The source of the outermost command it came from.</param>
/// <param name="Arguments">The program and its arguments after expansion (a dynamic word shows as written).</param>
public sealed record AnalyzedCommand(
    string Verb,
    string? Subcommand,
    ActionEffects Effects,
    ImmutableArray<ActionTarget> Targets,
    ImmutableArray<string> Unresolved,
    ImmutableArray<string> Via,
    bool Substitution,
    string Text,
    ImmutableArray<string> Arguments = default)
{
    public bool ReadOnly => (Effects & ~ActionEffects.Read) == ActionEffects.None;
}

/// <summary>The structure of a proposed action: every command it runs, or why it could not be parsed.</summary>
/// <param name="Kind"><c>shell</c> or <c>tool</c>.</param>
/// <param name="Unsupported">For a shell command outside the parsed subset, the parser's reason; no commands are listed.</param>
public sealed record ActionAnalysis(string Kind, string Text, ImmutableArray<AnalyzedCommand> Commands, string? Unsupported)
{
    /// <summary>Script text that could not be parsed (the whole command, or a nested <c>sh -c</c> string). The hardline backstop scans it.</summary>
    public ImmutableArray<string> OpaqueScripts { get; init; } = [];

    public ActionEffects Effects => Commands.Aggregate(ActionEffects.None, (all, command) => all | command.Effects);

    public IEnumerable<ActionTarget> Targets => Commands.SelectMany(command => command.Targets);
}
