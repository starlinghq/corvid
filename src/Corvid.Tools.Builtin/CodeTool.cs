using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Approvals;
using Corvid.Sandbox;
using Corvid.Tools.CodeMode;

namespace Corvid.Tools.Builtin;

/// <summary>A7: the languages code mode runs.</summary>
public enum CodeLanguage
{
    CSharp,
    Python,
    JavaScript,
}

/// <summary>
/// A7: what Corvid runs so a script can call tools: the program a generated client (code mode's client generators) makes
/// of the script, which keeps the script's own line numbers (A7 review L4).
/// </summary>
/// <param name="program">The whole program given the runtime for a script.</param>
public sealed class CodeClient(Func<string, string> program)
{
    private readonly Func<string, string> _program = program ?? throw new ArgumentNullException(nameof(program));

    /// <summary>A client whose text goes before and after the script.</summary>
    public static CodeClient Around(string prefix, string suffix) => new(script => prefix + script + suffix);

    /// <summary>The program for <paramref name="script"/>.</summary>
    public string Program(string script) => _program(script);
}

/// <summary>
/// A7: produces the client a script of <paramref name="language"/> is run with, for the tools it may call (their names and
/// descriptions are untrusted text the producer escapes). Null runs the script as written: it speaks the wire format itself.
/// </summary>
public delegate CodeClient? CodeClientFactory(CodeLanguage language, IReadOnlyList<CodeModeTool> tools);

/// <summary>
/// A7: the language runtimes code mode runs, by absolute path (never looked up on the sandbox's PATH, which a workspace
/// could shadow). A null one refuses its language.
/// </summary>
public sealed record CodeRuntimes(string? Python, string? Node, string? Dotnet)
{
    /// <summary>The runtimes found on this machine: <c>python3</c> and <c>node</c> on PATH, and the .NET host (see <see cref="FindDotnet"/>).</summary>
    public static CodeRuntimes Discover(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return new(OnPath("python3", environment), OnPath("node", environment), FindDotnet(environment));
    }

    /// <summary>
    /// The .NET host: <c>$DOTNET_ROOT/dotnet</c>, <c>dotnet</c> on PATH, then <c>~/.dotnet/dotnet</c>, the first that has an
    /// SDK beside it (a host without <c>sdk/</c> cannot run a file-based app).
    /// </summary>
    public static string? FindDotnet(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        string?[] candidates =
        [
            environment("DOTNET_ROOT") is { Length: > 0 } root ? Path.Combine(root, "dotnet") : null,
            OnPath("dotnet", environment),
            environment("HOME") is { Length: > 0 } home ? Path.Combine(home, ".dotnet", "dotnet") : null,
        ];
        foreach (var candidate in candidates)
        {
            if (candidate is null || !File.Exists(candidate))
                continue;
            var resolved = Resolve(candidate);
            if (Directory.Exists(Path.Combine(Path.GetDirectoryName(resolved)!, "sdk")))
                return resolved;
        }
        return null;
    }

    /// <summary>The runtime of a language, or null when it is absent.</summary>
    public string? For(CodeLanguage language) => language switch
    {
        CodeLanguage.Python => Python,
        CodeLanguage.JavaScript => Node,
        CodeLanguage.CSharp => Dotnet,
        _ => null,
    };

    /// <summary>An absolute, resolved path to an executable named <paramref name="program"/> on PATH, or null.</summary>
    public static string? OnPath(string program, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        foreach (var directory in (environment("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, program);
            if (File.Exists(candidate) && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return Resolve(candidate);
        }
        return null;
    }

    private static string Resolve(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }
}

/// <summary>A7: how code mode runs scripts (<c>Corvid:Tools:Code</c>). Hermes' defaults.</summary>
public sealed record CodeToolOptions
{
    /// <summary>The tools a script may call, besides being offered in its turn (code mode's allowlist).</summary>
    public static ImmutableArray<string> DefaultTools { get; } =
        ["list_files", "read_file", "search_files", "write_file", "patch_file", "web_fetch", "shell", "memory_search", "search_memory"];

    /// <summary>
    /// The run's time limit, wall clock, including time a tool call waits for its approval. Enforced by the sandbox the tool
    /// is given (its <see cref="SandboxOptions.Timeout"/>, which the host sets to this); here for the result's wording, and
    /// as the nested calls' limit (<see cref="ToolCallContext.NestedTimeLimit"/>): an approval a call waits on is denied when
    /// the run would be stopped, and its card counts down to then (A7V1-3).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Tool calls a run may make.</summary>
    public int MaxCalls { get; init; } = 50;

    /// <summary>Bytes of standard output the result keeps (the middle of longer output is cut).</summary>
    public int MaxStdoutBytes { get; init; } = 50 * 1000;

    /// <summary>Bytes of standard error the result keeps.</summary>
    public int MaxStderrBytes { get; init; } = 10 * 1000;

    /// <summary>The tools a script may call (intersected with the turn's offered tools).</summary>
    public ImmutableArray<string> Tools { get; init; } = DefaultTools;

    /// <summary>Connections a run may hold open at once to its tools socket; more are closed unanswered.</summary>
    public int MaxConnections { get; init; } = 8;

    public CodeRuntimes Runtimes { get; init; } = new(null, null, null);

    /// <summary>The client a script is run with (code mode's client generators); null runs scripts as written.</summary>
    public CodeClientFactory? Clients { get; init; }

    /// <summary>Where the per-run tools socket directories are made: a directory only this user can enter, hidden from this run's command. Default: <see cref="SandboxScratch"/>'s root, which every sandbox hides.</summary>
    public string? SocketRoot { get; init; }

    /// <summary>The wire format's limits for a run: <see cref="MaxCalls"/> and the defaults for the rest.</summary>
    public CodeModeLimits Limits => CodeModeLimits.Default with { MaxCalls = MaxCalls };
}

/// <summary>
/// A7: the <c>execute_code</c> tool (plan §15, parity row 1.6). A script in C#, Python or JavaScript runs in the sandbox —
/// the shell's policy for the workspace (its denials, its read-only paths), with no network at all and no Vault route — and
/// calls tools only through the run's tools socket, served here by <see cref="CodeModeSession"/> with a per-run token. Every
/// call goes through the turn's own call path (<see cref="ToolCallContext.InvokeToolAsync"/>): the approval pipeline per
/// call, checkpoints, the credential scrub, taint, egress, leases. The script itself runs only with an approval for
/// exactly this source, assessed as code (<see cref="BuiltinTools.CodeToolMapping"/>), which always asks.
/// </summary>
public sealed partial class CodeTool
{
    public const string ToolName = "execute_code";
    public const string LanguageParameter = "language";
    public const string CodeParameter = "code";

    private readonly CodeRunner _runner;
    private readonly Func<ToolRegistry> _registry;

    /// <param name="shell">The workspace's shell: its policy is the script's, without network.</param>
    /// <param name="sandbox">The sandbox scripts run in; its timeout is the run's time limit.</param>
    /// <param name="registry">The tools a script's calls are described from (the session's registry).</param>
    public CodeTool(SandboxedShell shell, ISandbox sandbox, CodeToolOptions options, Func<ToolRegistry> registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _runner = new CodeRunner(shell, sandbox, options);
        _registry = registry;
    }

    public CodeToolOptions Options => _runner.Options;

    /// <summary>Example requests for the shortlist (<c>TurnOptions.ToolExamples</c>), in the user's words.</summary>
    public static ImmutableArray<string> Examples { get; } =
    [
        "write a script that totals the amounts in every csv file", "loop over the markdown files and list their headings",
        "run some python to analyse this data", "count the TODOs in each file and sort them", "compute statistics over the log files",
        "write a program that reads all the json files and merges them",
    ];

    [Tool(ToolName, Description = "Run a short program (language: python, javascript or csharp) that calls Corvid's tools, for work " +
        "that needs a loop or several tool calls: processing many files, combining results, computing over data. It runs sandboxed in the " +
        "workspace with no network; its tool calls are approved one by one like your own. Returns its output and a log of its calls. " +
        "Call a tool by its name: in Python corvid.read_file(path='x') (errors raise corvid.CorvidToolError); in JavaScript, an ES module, " +
        "await corvid.read_file({ path: 'x' }); in C# await CorvidTools.ReadFileAsync(\"x\"), which returns a JsonElement.")]
    [SideEffect(SideEffects.Process | SideEffects.Filesystem)]
    [Destructive]
    public async Task<JsonNode> RunAsync(
        [Description("The program's language: python, javascript or csharp.")] string language,
        [Description("The program's source. It starts in the workspace root; it prints its results to standard output.")] string code,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parsed = ParseLanguage(language);
        if (string.IsNullOrWhiteSpace(code))
            throw new ToolArgumentException("Argument 'code' must not be empty.", CodeParameter);
        // Exactly this script, assessed as code, approved; never a shell approval or a tool approval that mined the source.
        if (context.Approval is not ApprovalResult { Allowed: true, Assessment.Analysis: { Kind: "code" } analysis }
            || analysis.Text != code || analysis.Commands is not [{ Subcommand: var approvedLanguage }] || approvedLanguage != language)
            throw new InvalidOperationException("A script runs only after the approval pipeline has allowed exactly this script.");
        if (context.Invoker is null)
            throw new InvalidOperationException("A script runs only as a call the model made in a turn: its tool calls need the turn's call path.");
        return (await _runner.RunAsync(parsed, code, context, _registry(), cancellationToken).ConfigureAwait(false)).ToJson();
    }

    internal static CodeLanguage ParseLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "python" or "python3" or "py" => CodeLanguage.Python,
        "javascript" or "js" or "node" => CodeLanguage.JavaScript,
        "csharp" or "c#" or "cs" or "dotnet" => CodeLanguage.CSharp,
        _ => throw new ToolArgumentException($"Argument 'language' must be python, javascript or csharp, not '{language}'.", LanguageParameter),
    };

    /// <summary>The wire name of a language.</summary>
    public static string Name(CodeLanguage language) => language switch
    {
        CodeLanguage.Python => "python",
        CodeLanguage.JavaScript => "javascript",
        _ => "csharp",
    };
}

/// <summary>A7: one nested call a script made, as the result's call log shows it.</summary>
public sealed record CodeCall(string CallId, string Tool, bool Ok, bool Tainted);

/// <summary>A7: what a script run produced.</summary>
public sealed record CodeRun
{
    public required CodeLanguage Language { get; init; }

    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public bool OutputTruncated { get; init; }

    public bool TimedOut { get; init; }

    /// <summary>The run's time limit, for a timed-out result's message.</summary>
    public TimeSpan Limit { get; init; }

    public ImmutableArray<CodeCall> Calls { get; init; } = [];

    /// <summary>A tool result the script received was outside content: so is everything the script printed.</summary>
    public bool Tainted { get; init; }

    public JsonNode ToJson()
    {
        var output = new JsonObject
        {
            ["language"] = CodeTool.Name(Language),
            ["exit_code"] = ExitCode,
            ["stdout"] = StandardOutput,
            ["stderr"] = StandardError,
        };
        if (TimedOut)
        {
            output["timed_out"] = true;
            output["error"] = string.Create(CultureInfo.InvariantCulture,
                $"the script ran past its time limit of {Limit.TotalSeconds:0} s (including any wait for approvals) and was stopped");
        }
        if (OutputTruncated)
            output["output_truncated"] = true;
        output["tool_calls"] = Calls.Length;
        output["calls"] = new JsonArray([.. Calls.Select(call => (JsonNode)new JsonObject
        {
            ["call_id"] = call.CallId,
            ["tool"] = call.Tool,
            ["ok"] = call.Ok,
            ["tainted"] = call.Tainted,
        })]);
        output["tainted"] = Tainted;
        return output;
    }
}

/// <summary>A7: runs one script with its tools socket. Separate from the tool so tests can drive it with a context of their own.</summary>
public sealed class CodeRunner
{
    // C#: a fixed wrapper, never the script, run by /bin/sh inside the sandbox. It writes the source from standard input to
    // the sandbox's own /tmp (outside the workspace, so the workspace's Directory.Build.* and NuGet settings are not read),
    // with stub build files that stop MSBuild's upward search, and builds there (its output on stderr only when it fails) from
    // there, so the workspace's global.json does not pick the SDK either. Then it runs the built app in the workspace through
    // the host, which reads the app's runtimeconfig, never a global.json.
    // The SDK's caches go under the workspace's .corvid/code, the only writable place that lasts; the sandbox writes them.
    private const string CSharpWrapper =
        """
        set -eu
        dotnet="$1"; cache="$2"; workspace="$PWD"
        app=/tmp/corvid-code; mkdir -p "$app" "$cache" "$XDG_DATA_HOME"
        [ -e "$cache/../.gitignore" ] || printf '*\n' > "$cache/../.gitignore"
        cat > "$app/Program.cs"
        printf '%s\n' '<Project><PropertyGroup><PublishAot>false</PublishAot><UseSharedCompilation>false</UseSharedCompilation><TreatWarningsAsErrors>false</TreatWarningsAsErrors><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>' > "$app/Directory.Build.props"
        printf '%s\n' '<Project />' > "$app/Directory.Build.targets"
        printf '%s\n' '<Project />' > "$app/Directory.Packages.props"
        printf '%s\n' '<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear /></packageSources></configuration>' > "$app/nuget.config"
        cd "$app"
        "$dotnet" build "$app/Program.cs" --artifacts-path "$app/artifacts" -v q -nologo -clp:NoSummary > "$app/build.log" 2>&1 || { cat "$app/build.log" >&2; exit 1; }
        built="$app/artifacts/bin/debug/Program.dll"
        [ -f "$built" ] || { cat "$app/build.log" >&2; echo "corvid-code: the build left no $built" >&2; exit 1; }
        cd "$workspace"
        exec "$dotnet" "$built"
        """;

    private readonly SandboxedShell _shell;
    private readonly ISandbox _sandbox;

    public CodeRunner(SandboxedShell shell, ISandbox sandbox, CodeToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxStdoutBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxStderrBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConnections);
        _shell = shell;
        _sandbox = sandbox;
        Options = options;
    }

    public CodeToolOptions Options { get; }

    /// <summary>
    /// Runs <paramref name="code"/> with a tools socket whose calls go to <paramref name="context"/>'s invoker. Every nested
    /// call has its result before this returns.
    /// </summary>
    /// <exception cref="ToolArgumentException">The script uses a directive code mode refuses (C# <c>#:</c>).</exception>
    /// <exception cref="InvalidOperationException">The language's runtime is not installed.</exception>
    /// <exception cref="SandboxRefusedException">The sandbox could not be set up; nothing ran.</exception>
    public async Task<CodeRun> RunAsync(CodeLanguage language, string code, ToolCallContext context, ToolRegistry registry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);
        if (language == CodeLanguage.CSharp && Directive(code) is { } directive)
            throw new ToolArgumentException(
                $"C# scripts may not use '#:' directives ('{directive}'): code mode runs the SDK's own references only, with no packages, SDKs or projects.",
                CodeTool.CodeParameter);
        var runtime = Options.Runtimes.For(language)
            ?? throw new InvalidOperationException($"No {CodeTool.Name(language)} runtime is installed for code mode ({Install(language)}); `corvid doctor` lists the runtimes.");

        var tools = CodeModeCatalog.Describe(registry, context.OfferedTools, Options.Tools);
        var calls = new List<CodeCall>();
        var invoke = context.Invoker ?? throw new InvalidOperationException("A script's tool calls need the turn's call path.");
        // A7V1-3: a nested call's approval closes when the run would be stopped, so its card counts down to the run's end.
        context.NestedTimeLimit = Options.Timeout;
        async Task<NestedToolResult> Record(string tool, System.Text.Json.JsonElement arguments, CancellationToken token)
        {
            var result = await invoke(tool, arguments, token).ConfigureAwait(false);
            lock (calls)
                calls.Add(new CodeCall(result.CallId, tool, !result.IsError, result.Tainted));
            return result;
        }

        var token = CodeModeProtocol.NewToken();
        var session = new CodeModeSession(token, tools, Record, Options.Limits);
        var client = Options.Clients?.Invoke(language, tools);
        var source = client is null ? code : client.Program(code);

        // A directory only this user can enter, outside the workspace, deleted after the run. A7V1-2: by default under the
        // root every sandbox hides (SandboxScratch), so no other run's command sees this socket, whatever TMPDIR is.
        if (OperatingSystem.IsWindows())
            throw new SandboxRefusedException("Code mode needs a Unix socket and the Linux sandbox.");
        var socketDirectory = Options.SocketRoot is { } root
            ? Directory.CreateDirectory(Path.Combine(root, "corvid-code-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            : SandboxScratch.CreateDirectory("code-");
        try
        {
            var socketPath = Path.Combine(socketDirectory.FullName, "tools.sock");
            // sockaddr_un holds 107 bytes of path.
            if (Encoding.UTF8.GetByteCount(socketPath) > 100)
                throw new SandboxRefusedException($"The tools socket's path is too long for a Unix socket ({socketPath}); set TMPDIR to a shorter directory.");
            var basePolicy = _shell.Policy;
            // The default root is hidden from every run's command (the sandbox adds it from its own environment; it is named
            // here too, from this process's, in case the two differ). A socket root a caller chose is hidden from this run's.
            var hidden = Options.SocketRoot is null
                ? SandboxScratch.DeniedRoot(basePolicy) is { } scratch ? basePolicy.DeniedPaths.Add(scratch) : basePolicy.DeniedPaths
                : IsUnder(socketDirectory.FullName, "/tmp") || IsUnder(socketDirectory.FullName, "/run")
                    ? basePolicy.DeniedPaths
                    : basePolicy.DeniedPaths.Add(socketDirectory.FullName);
            var values = EnvironmentFor(language, runtime, basePolicy.Workspace);
            var policy = basePolicy with
            {
                Network = false,
                Proxy = null,
                Tools = new SandboxTools(socketPath, token),
                DeniedPaths = hidden,
                // The runtime's own settings win over a variable the shell passes through under the same name.
                Environment = [.. basePolicy.Environment.Where(name => !values.ContainsKey(name))],
                EnvironmentValues = values,
            };
            var command = CommandFor(language, runtime, basePolicy.Workspace, source) with { Output = context.Output };

            await using var server = ToolsSocketServer.Start(socketPath, session, Options.MaxConnections);
            SandboxResult result;
            try
            {
                // Vault's sandbox directory exists before the launcher runs, so it is denied even before Vault's first proxy.
                _shell.Options.Vault?.EnsureSandboxDirectory();
                result = await _sandbox.RunAsync(policy, command, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // The script is gone: its connections close, and a call still running is cancelled and has its result.
                // A7 review (L3): never throws, so the run's output and exit code survive a connection that failed.
                await server.StopAsync().ConfigureAwait(false);
            }

            var (stdout, stdoutCut) = Trim(result.StandardOutput, Options.MaxStdoutBytes);
            // A connection the host failed to serve says so after the script's own error output.
            var failures = server.Failures;
            var (stderr, stderrCut) = Trim(failures.Count == 0 ? result.StandardError
                : result.StandardError + string.Concat(failures.Select(failure => $"\n[corvid] the tools socket failed: {failure}")), Options.MaxStderrBytes);
            ImmutableArray<CodeCall> made;
            lock (calls)
                made = [.. calls];
            return new CodeRun
            {
                Language = language,
                ExitCode = result.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr,
                OutputTruncated = stdoutCut || stderrCut || result.StandardOutputTruncated || result.StandardErrorTruncated,
                TimedOut = result.TimedOut,
                Limit = Options.Timeout,
                Calls = made,
                Tainted = context.ResultTainted || made.Any(call => call.Tainted),
            };
        }
        finally
        {
            try
            {
                socketDirectory.Delete(recursive: true);
            }
            catch (Exception)
            {
                // Best effort: an empty 0700 directory under the private root, which drops run directories a day old (A7 review L5).
            }
        }
    }

    /// <summary>The command that runs a script given on standard input, by the runtime's absolute path.</summary>
    internal static SandboxCommand CommandFor(CodeLanguage language, string runtime, string workspace, string source) => language switch
    {
        // -I: isolated (no PYTHON* variables, no user site, and neither the working directory nor the script's on sys.path,
        // so a workspace's json.py never shadows the client's imports; A7 review L4); -B: no bytecode written into the
        // workspace. "-" reads standard input.
        CodeLanguage.Python => new SandboxCommand(runtime, "-I", "-B", "-") { StandardInput = source, WorkingDirectory = workspace },
        // An ES module, so a script can await at the top level.
        CodeLanguage.JavaScript => new SandboxCommand(runtime, "--input-type=module", "-") { StandardInput = source, WorkingDirectory = workspace },
        _ => new SandboxCommand("/bin/sh", "-c", CSharpWrapper, "corvid-code", runtime, DotnetCache(workspace))
        {
            StandardInput = source,
            WorkingDirectory = workspace,
        },
    };

    /// <summary>The SDK's caches for a workspace (written by the sandbox, never by Corvid).</summary>
    internal static string DotnetCache(string workspace) => Path.Combine(workspace, ".corvid", "code", "dotnet");

    /// <summary>Non-secret settings for the runtime: values on the launcher's command line, never Corvid's own.</summary>
    internal static ImmutableSortedDictionary<string, string> EnvironmentFor(CodeLanguage language, string runtime, string workspace)
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        values["PYTHONDONTWRITEBYTECODE"] = "1";
        values["NO_COLOR"] = "1";
        if (language == CodeLanguage.CSharp)
        {
            var cache = DotnetCache(workspace);
            values["DOTNET_ROOT"] = Path.GetDirectoryName(runtime)!;
            values["DOTNET_CLI_HOME"] = Path.Combine(cache, "home");
            // The SDK's per-user data (file-based apps' build cache, NuGet's migrations) in the sandbox's own /tmp: the home
            // directory is read-only there, and two runs in one workspace must not share a build of the same path.
            values["XDG_DATA_HOME"] = "/tmp/corvid-code/data";
            // Named mutexes (NuGet's first-use migrations) live in the runtime directory; the sandbox's /run is empty and
            // read-only, so the sandbox's /tmp stands in.
            values["XDG_RUNTIME_DIR"] = "/tmp";
            values["NUGET_PACKAGES"] = Path.Combine(cache, "packages");
            values["DOTNET_NOLOGO"] = "1";
            values["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            values["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            values["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            values["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
            values["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            values["MSBUILDDISABLENODEREUSE"] = "1";
        }
        return values.ToImmutable();
    }

    /// <summary>
    /// The first <c>#:</c> directive line of a C# file-based app (a package, SDK, project or property), or null. Lines end
    /// where Roslyn ends them (A7 review, security L2): at <c>\n</c>, <c>\r</c>, U+0085, U+2028 and U+2029, so a directive
    /// after a lone carriage return or a line separator is found.
    /// </summary>
    internal static string? Directive(string code)
    {
        foreach (var line in code.Split(['\n', '\r', '\u0085', '\u2028', '\u2029']))
        {
            // Any white space (a vertical tab, a form feed, U+00A0), byte order marks and U+001A (which Roslyn also reads as
            // white space) may come before the directive, in any mix.
            var trimmed = line;
            for (var before = -1; before != trimmed.Length;)
            {
                before = trimmed.Length;
                trimmed = trimmed.TrimStart().TrimStart('\uFEFF', '\u001A');
            }
            if (trimmed.StartsWith("#:", StringComparison.Ordinal))
                return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
        }
        return null;
    }

    private static string Install(CodeLanguage language) => language switch
    {
        CodeLanguage.Python => "install python3",
        CodeLanguage.JavaScript => "install node",
        _ => "install the .NET 10 SDK",
    };

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory + "/", StringComparison.Ordinal);

    /// <summary>Keeps the start and, mostly, the end of output longer than <paramref name="maxBytes"/> UTF-8 bytes.</summary>
    internal static (string Text, bool Cut) Trim(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return (text, false);
        var head = Take(text, maxBytes / 4, fromEnd: false);
        var tail = Take(text, maxBytes - maxBytes / 4, fromEnd: true);
        var cut = Encoding.UTF8.GetByteCount(text) - Encoding.UTF8.GetByteCount(head) - Encoding.UTF8.GetByteCount(tail);
        return (string.Create(CultureInfo.InvariantCulture, $"{head}\n… [{cut} bytes cut] …\n{tail}"), true);
    }

    private static string Take(string text, int bytes, bool fromEnd)
    {
        var count = 0;
        var chars = 0;
        var rune = fromEnd ? text.EnumerateRunes().Reverse() : text.EnumerateRunes();
        foreach (var r in rune)
        {
            if (count + r.Utf8SequenceLength > bytes)
                break;
            count += r.Utf8SequenceLength;
            chars += r.Utf16SequenceLength;
        }
        return fromEnd ? text[^chars..] : text[..chars];
    }
}
