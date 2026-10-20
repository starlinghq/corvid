using System.Collections.Frozen;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Corvid.Approvals;
using Corvid.Kernel.Jobs;
using Corvid.Sandbox;

namespace Corvid.Tools.Builtin;

/// <summary>How the shell runs commands. Network is off: per-toolset network and Vault's proxy route (V-003) come later.</summary>
public sealed record ShellOptions
{
    /// <summary>The directory commands start in and the only one they can write. Absolute.</summary>
    public required string Workspace { get; init; }

    /// <summary>Vault's configured locations, denied to every command in addition to the launcher's built-in denials.</summary>
    public VaultLocations? Vault { get; init; }

    /// <summary>More paths to hide from commands. Absolute.</summary>
    public ImmutableArray<string> DeniedPaths { get; init; } = [];

    /// <summary>Paths inside the workspace that commands may read but not change (the skills directory, say). Absolute; they must exist.</summary>
    public ImmutableArray<string> ReadOnlyPaths { get; init; } = [];

    /// <summary>
    /// The shell that runs the command line. bash, because the approval pipeline parses the bash subset (D-5): a shell
    /// that reads <c>$'...'</c> or here-strings differently could run something other than what was approved.
    /// </summary>
    public string ShellPath { get; init; } = "/bin/bash";

    /// <summary>Characters of output a result keeps per stream; the middle of longer output is cut.</summary>
    public int MaxResultChars { get; init; } = 16_000;

    /// <summary>
    /// Environment variables passed to commands (secret-like names are refused by the sandbox, and names that change what
    /// bash runs by <see cref="SandboxedShell"/>).
    /// </summary>
    public ImmutableArray<string> Environment { get; init; } = SandboxPolicy.DefaultEnvironment;
}

/// <summary>
/// Runs a command line under the sandbox (C-007a) with Corvid's policy: the workspace writable, Vault's configured
/// locations and any extra denied paths hidden, no network. It does not ask anyone: callers authorize first, through the
/// approval pipeline (the shell tool by the turn loop's pre-execute checkpoint, the scheduler's scripts non-interactively).
/// </summary>
public sealed class SandboxedShell
{
    /// <summary>
    /// Variables that make bash (or the loader) run or read something other than the command line the approval pipeline
    /// parsed: a startup file, shell options, globbing and <c>cd</c> rules, or preloaded code.
    /// </summary>
    private static readonly FrozenSet<string> UnparsedBehaviour = FrozenSet.ToFrozenSet(
        ["BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS", "GLOBIGNORE", "CDPATH", "PS4", "LD_PRELOAD", "LD_AUDIT", "LD_LIBRARY_PATH"],
        StringComparer.Ordinal);

    private readonly ISandbox _sandbox;

    public SandboxedShell(ISandbox sandbox, ShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxResultChars);
        if (options.Environment.FirstOrDefault(UnparsedBehaviour.Contains) is { } unparsed)
            throw new ArgumentException($"{unparsed} would change what bash runs beyond the approved command line, so it is not passed to commands.", nameof(options));
        _sandbox = sandbox;
        Options = options;
        Policy = new SandboxPolicy
        {
            Workspace = Path.GetFullPath(options.Workspace),
            ReadOnlyPaths = options.ReadOnlyPaths,
            DeniedPaths = [.. (options.Vault?.Paths ?? []).Concat(options.DeniedPaths).Distinct(StringComparer.Ordinal)],
            Network = false,
            Environment = options.Environment,
        };
        Policy.Validate();
    }

    public ShellOptions Options { get; }

    /// <summary>The policy every command runs under.</summary>
    public SandboxPolicy Policy { get; }

    /// <exception cref="SandboxRefusedException">The sandbox could not be set up; nothing ran.</exception>
    public Task<SandboxResult> RunAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return _sandbox.RunAsync(Policy, new SandboxCommand(Options.ShellPath, "-c", command), cancellationToken);
    }

    /// <summary>Keeps the start and, mostly, the end of long output, where errors usually are.</summary>
    internal string Trim(string text)
    {
        var max = Options.MaxResultChars;
        if (text.Length <= max)
            return text;
        var head = max / 4;
        var tail = max - head;
        var cut = text.Length - head - tail;
        return string.Create(CultureInfo.InvariantCulture, $"{text[..head]}\n… [{cut} characters cut] …\n{text[^tail..]}");
    }
}

/// <summary>
/// The <c>shell</c> tool (plan §10.1, §10.2): a command line run by bash inside the sandbox. It runs only with an approval
/// for exactly this command: the turn loop authorizes the call through the approval pipeline, which assesses it as the
/// parsed command (register <see cref="BuiltinTools.ShellToolMapping"/> in <see cref="ApprovalOptions.ShellTools"/>), and
/// passes the result on the call's context. A call without one (made outside a turn, or with a command other than the
/// one approved) is refused, never run.
/// </summary>
public sealed partial class ShellTool(SandboxedShell shell)
{
    public const string ToolName = "shell";
    public const string CommandParameter = "command";

    private readonly SandboxedShell _shell = shell ?? throw new ArgumentNullException(nameof(shell));

    [Tool(ToolName, Description = "Run a shell command line (bash) in the workspace and return its exit code and output. " +
        "It runs sandboxed: no network, writes only inside the workspace, secrets hidden. Prefer the file tools for reading and editing files.")]
    [SideEffect(SideEffects.Process | SideEffects.Filesystem)]
    [Destructive]
    public async Task<JsonNode> RunAsync(
        [Description("The command line, for example 'dotnet test' or 'ls -la src'. It starts in the workspace root.")] string command,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(command))
            throw new ToolArgumentException("Argument 'command' must not be empty.", CommandParameter);
        if (context.Approval is not ApprovalResult { Allowed: true, Assessment.Analysis: { Kind: "shell" } analysis } || analysis.Text != command)
            throw new InvalidOperationException("The shell runs a command only after the approval pipeline has allowed exactly that command line.");

        var result = await _shell.RunAsync(command, cancellationToken).ConfigureAwait(false);
        var output = new JsonObject
        {
            ["exit_code"] = result.ExitCode,
            ["stdout"] = _shell.Trim(result.StandardOutput),
            ["stderr"] = _shell.Trim(result.StandardError),
        };
        if (result.TimedOut)
            output["timed_out"] = true;
        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            output["output_truncated"] = true;
        return output;
    }
}

/// <summary>
/// Runs scheduled jobs' pre-run scripts (plan §13): authorized through the approval pipeline with nobody to ask, so only a
/// read-only script, or one an always-rule allows, runs; then run in the sandbox like the shell tool's commands. Without a
/// calibrated decider every side-effecting script is refused.
/// </summary>
public sealed class SandboxedScriptRunner : IJobScriptRunner
{
    private readonly SandboxedShell _shell;
    private readonly ApprovalPipeline _approvals;
    private readonly ApprovalMode _mode;

    public SandboxedScriptRunner(SandboxedShell shell, ApprovalPipeline approvals, ApprovalMode mode = ApprovalMode.Smart)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(approvals);
        // The pipeline resolves paths against its workspace; a script run elsewhere would not be the one assessed.
        if (Path.GetFullPath(approvals.Environment.Workspace).TrimEnd('/') != shell.Policy.Workspace.TrimEnd('/'))
            throw new ArgumentException("The approval pipeline and the shell must use the same workspace.", nameof(approvals));
        _shell = shell;
        _approvals = approvals;
        _mode = mode;
    }

    public async Task<JobScriptResult> RunAsync(JobScriptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = new ApprovalContext(request.SessionId, _mode, request.State) { Interactive = false, Tainted = request.Tainted };
        var approval = await _approvals.AuthorizeAsync(context, new ShellAction(request.Script), cancellationToken).ConfigureAwait(false);
        if (!approval.Allowed)
            return JobScriptResult.Refused($"{WireNames(approval.Resolution)}: {approval.Assessment.Summary}");
        SandboxResult result;
        try
        {
            result = await _shell.RunAsync(request.Script, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxRefusedException exception)
        {
            return JobScriptResult.Refused("the sandbox refused: " + exception.Message);
        }

        var output = _shell.Trim(result.StandardOutput);
        if (result.StandardError.Length > 0)
            output += (output.Length > 0 ? "\n" : "") + "[stderr]\n" + _shell.Trim(result.StandardError);
        if (result.TimedOut)
            output += "\n[timed out]";
        return new JobScriptResult(true, result.ExitCode, output);
    }

    private static string WireNames(ApprovalResolution resolution) => resolution switch
    {
        ApprovalResolution.Hardline => "refused by the hardline list",
        ApprovalResolution.RuleDenied => "refused by a remembered rule",
        ApprovalResolution.NoApprover => "it needs approval and nobody can answer at a scheduled run",
        _ => "refused",
    };
}
