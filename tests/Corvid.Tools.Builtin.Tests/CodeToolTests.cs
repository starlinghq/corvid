using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Corvid.Approvals;
using Corvid.Kernel.Events;
using Corvid.Sandbox;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

/// <summary>What the code tests share: the code approval as the turn loop gets it, and a turn's context with a fake invoker.</summary>
internal static class CodeFixtures
{
    public static ApprovalPipeline Pipeline(SqliteEventLog log, ApprovalEnvironment environment, IApprovalPrompter? prompter = null) =>
        new(environment, DegradedFabric(log), log, prompter,
            options: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping, CodeTools = BuiltinTools.CodeToolMapping });

    public static ToolDescriptor Descriptor(CodeTool tool) => tool.CreateTools().Single().Descriptor;

    public static string Arguments(string language, string code) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["language"] = language, ["code"] = code });

    /// <summary>The pipeline's answer for exactly this script, as the turn loop passes it on the call's context.</summary>
    public static async Task<ApprovalResult> ApproveAsync(ApprovalPipeline pipeline, CodeTool tool, string language, string code)
    {
        var result = await pipeline.AuthorizeAsync(new ApprovalContext(Session, ApprovalMode.Smart, State()) { CallId = "call-1" },
            new ToolAction(Descriptor(tool), Json(Arguments(language, code))), Ct);
        Assert.True(result.Allowed, result.Assessment.Summary);
        return result;
    }

    /// <summary>The descriptors scripts are told about: the file tools and web_fetch of a workspace (they never run here).</summary>
    public static ToolRegistry Registry(string workspace)
    {
        var registry = new ToolRegistry();
        registry.Add(new FileTools(new Workspace(workspace)));
        registry.Add(new WebFetchTool());
        return registry;
    }

    /// <summary>
    /// A script's raw client in Python: the wire format as docs/architecture.md freezes it (code mode's generated clients
    /// wrap the same lines). <c>call</c> returns the response object.
    /// </summary>
    public const string PythonClient =
        """
        import json, os, socket
        _s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        _s.connect(os.environ["CORVID_TOOLS_SOCKET"])
        _f = _s.makefile("rwb")
        def _line(o):
            _f.write((json.dumps(o) + "\n").encode()); _f.flush()
            return json.loads(_f.readline())
        hello = _line({"token": open(os.environ["CORVID_TOOLS_TOKEN_FILE"]).read().strip(), "v": 1})
        _n = 0
        def call(tool, args=None):
            global _n
            _n += 1
            return _line({"id": _n, "tool": tool, "args": args or {}})

        """;
}

/// <summary>A7: execute_code's own refusals and what it hands the sandbox, without running anything.</summary>
public sealed class CodeToolTests : IDisposable
{
    private const string Workspace = "/home/user/work/project";

    private readonly TempTree _tree = new();
    private readonly SqliteEventLog _log;
    private readonly ApprovalPipeline _pipeline;
    private readonly RecordingSandbox _sandbox = new(command => new SandboxResult
    {
        ExitCode = 0,
        StandardOutput = "printed",
        StandardError = "",
    });

    private readonly CodeTool _tool;

    public CodeToolTests()
    {
        _log = new SqliteEventLog(_tree.Path("events.db"));
        _pipeline = CodeFixtures.Pipeline(_log, ApprovalEnvironment.Create(Workspace, "/home/user"), new Answering(approve: true));
        _tool = NewTool();
    }

    public void Dispose() => _tree.Dispose();

    private CodeTool NewTool(CodeRuntimes? runtimes = null) => new(
        new SandboxedShell(_sandbox, new ShellOptions
        {
            Workspace = Workspace,
            Vault = new VaultLocations("/srv/vault", "/etc/vault.json", "/run/vault.sock"),
            DeniedPaths = ["/home/user/private"],
            Network = true,
        }),
        _sandbox,
        new CodeToolOptions { Runtimes = runtimes ?? new CodeRuntimes("/usr/bin/python3", "/usr/bin/node", "/opt/dotnet/dotnet"), SocketRoot = null },
        () => CodeFixtures.Registry(_tree.Root));

    private async Task<ToolCallContext> ContextAsync(string language, string code, ToolInvoker? invoker = null) => new(Session, "call-1")
    {
        Approval = await CodeFixtures.ApproveAsync(_pipeline, _tool, language, code),
        Invoker = invoker ?? ((_, _, _) => throw new InvalidOperationException("no calls expected")),
        OfferedTools = new HashSet<string>(["read_file", "write_file"], StringComparer.Ordinal),
    };

    [Fact]
    public void The_tool_is_a_destructive_process_and_filesystem_side_effect_with_language_and_code()
    {
        var tool = Tool(_tool, CodeTool.ToolName);

        Assert.Equal(new ToolRisk(SideEffects.Process | SideEffects.Filesystem, Network: false, Destructive: true), tool.Descriptor.Risk);
        Assert.Equal(["language", "code"], tool.Descriptor.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task A_script_is_assessed_as_code_with_its_source_shown_nothing_mined_and_always_asked_once()
    {
        // Paths and URLs in the source are not targets: a script's strings are not what it does.
        const string code = "open('/etc/passwd').read()\nurl = 'https://example.com/x'\nopen('out.txt', 'w').write('x')\n";
        var action = new ToolAction(CodeFixtures.Descriptor(_tool), Json(CodeFixtures.Arguments("python", code)));

        var analysis = _pipeline.Analyze(action);
        Assert.Equal("code", analysis.Kind);
        Assert.Equal(code, analysis.Text);
        var command = Assert.Single(analysis.Commands);
        Assert.Equal(CodeTool.ToolName, command.Verb);
        Assert.Equal("python", command.Subcommand);
        Assert.Empty(analysis.Targets);
        Assert.NotEmpty(command.Unresolved);

        // Even in permissive mode it asks, and offers only "once": no remembered rule can ever run a script unasked.
        var assessment = await _pipeline.AssessAsync(new ApprovalContext(Session, ApprovalMode.Permissive, State()), action, Ct);
        Assert.Equal(ApprovalOutcome.Ask, assessment.Outcome);
        Assert.Equal([ApprovalScope.Once], assessment.Offered);

        // Without the mapping the call would be a tool call whose arguments are mined (the contrast the mapping exists for).
        var plain = Fixtures.Pipeline(_log, ApprovalEnvironment.Create(Workspace, "/home/user")).Analyze(action);
        Assert.Equal("tool", plain.Kind);
    }

    [Fact]
    public async Task The_hardline_backstop_scans_a_scripts_source()
    {
        var environment = new VaultLocations("/srv/vault-data", "/etc/vault.json", "/run/vault.sock").Protect(ApprovalEnvironment.Create(Workspace, "/home/user"));
        var pipeline = CodeFixtures.Pipeline(_log, environment);
        var action = new ToolAction(CodeFixtures.Descriptor(_tool), Json(CodeFixtures.Arguments("python", "print(open('/srv/vault-data/store.bin').read())")));

        var assessment = await pipeline.AssessAsync(new ApprovalContext(Session, ApprovalMode.Permissive, State()), action, Ct);

        Assert.Equal(ApprovalOutcome.Deny, assessment.Outcome);
        Assert.True(assessment.Hardline);
    }

    [Fact]
    public async Task A_script_runs_only_with_a_code_approval_for_exactly_its_source_and_language()
    {
        const string code = "print('hi')";
        // A shell approval for the same text, a code approval for other text, and one for another language: none runs it.
        var shell = (ApprovalResult)(await Approved(Fixtures.Pipeline(_log, ApprovalEnvironment.Create(Workspace, "/home/user"), new Answering(true)),
            "echo hi")).Approval!;
        var other = await CodeFixtures.ApproveAsync(_pipeline, _tool, "python", "print('other')");
        var javascript = await CodeFixtures.ApproveAsync(_pipeline, _tool, "javascript", code);
        foreach (var approval in new object?[] { null, shell, other, javascript })
        {
            var context = new ToolCallContext(Session, "call-1") { Approval = approval, Invoker = (_, _, _) => throw new InvalidOperationException() };
            var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(_tool, CodeTool.ToolName, CodeFixtures.Arguments("python", code), context));
            Assert.Contains("approval pipeline has allowed exactly this script", error.Message, StringComparison.Ordinal);
        }

        // Approved, but outside a turn's call path (no invoker): still nothing runs.
        var approved = new ToolCallContext(Session, "call-1") { Approval = await CodeFixtures.ApproveAsync(_pipeline, _tool, "python", code) };
        var outside = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(_tool, CodeTool.ToolName, CodeFixtures.Arguments("python", code), approved));
        Assert.Contains("turn's call path", outside.Message, StringComparison.Ordinal);
        Assert.Empty(_sandbox.Runs);
    }

    [Theory]
    [InlineData("#:package Newtonsoft.Json@13.0.3\nConsole.WriteLine(1);")]
    [InlineData("#:sdk Microsoft.NET.Sdk.Web\nConsole.WriteLine(1);")]
    [InlineData("Console.WriteLine(1);\n  #:project ../evil/evil.csproj")]
    [InlineData("#:property PublishAot=true\nConsole.WriteLine(1);")]
    public async Task CSharp_directives_are_refused_before_anything_runs(string code)
    {
        var context = await ContextAsync("csharp", code);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(_tool, CodeTool.ToolName, CodeFixtures.Arguments("csharp", code), context));

        Assert.Contains("'#:' directives", error.Message, StringComparison.Ordinal);
        Assert.Empty(_sandbox.Runs);
    }

    [Fact]
    public async Task A_language_whose_runtime_is_absent_is_refused()
    {
        var tool = NewTool(new CodeRuntimes("/usr/bin/python3", null, null));
        var context = new ToolCallContext(Session, "call-1")
        {
            Approval = await CodeFixtures.ApproveAsync(_pipeline, tool, "javascript", "console.log(1)"),
            Invoker = (_, _, _) => throw new InvalidOperationException(),
        };

        var error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(tool, CodeTool.ToolName, CodeFixtures.Arguments("javascript", "console.log(1)"), context));

        Assert.Contains("No javascript runtime", error.Message, StringComparison.Ordinal);
        Assert.Empty(_sandbox.Runs);
    }

    [Fact]
    public async Task A_script_runs_with_the_shells_denials_no_network_a_tools_socket_and_its_runtime_by_absolute_path()
    {
        const string code = "print('hi')";
        var context = await ContextAsync("python", code);

        var result = await Invoke(_tool, CodeTool.ToolName, CodeFixtures.Arguments("python", code), context);

        var (policy, command) = Assert.Single(_sandbox.Runs);
        // The shell's network is on here; a script never gets it, nor Vault's route.
        Assert.False(policy.Network);
        Assert.Null(policy.Proxy);
        Assert.NotNull(policy.Tools);
        Assert.True(SandboxTools.IsValidToken(policy.Tools!.Token));
        Assert.Equal(Workspace, policy.Workspace);
        Assert.Contains("/srv/vault", policy.DeniedPaths);
        Assert.Contains("/home/user/private", policy.DeniedPaths);
        // The source goes on standard input; the runtime by its absolute path, never PATH.
        Assert.Equal("/usr/bin/python3", command.FileName);
        Assert.Equal(code, command.StandardInput);
        Assert.Equal(Workspace, command.WorkingDirectory);
        Assert.DoesNotContain(code, command.Arguments);
        Assert.Equal("printed", result.GetProperty("stdout").GetString());
        Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
        Assert.False(result.GetProperty("tainted").GetBoolean());
        // The token is never on the command line or in the environment values.
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains(policy.Tools.Token, StringComparison.Ordinal));
        Assert.DoesNotContain(policy.EnvironmentValues.Values, value => value.Contains(policy.Tools.Token, StringComparison.Ordinal));
        // The socket's directory is gone after the run.
        Assert.False(Directory.Exists(Path.GetDirectoryName(policy.Tools.SocketPath)));
    }

    [Fact]
    public void The_dotnet_command_runs_outside_the_workspace_with_its_caches_inside_it()
    {
        var command = CodeRunner.CommandFor(CodeLanguage.CSharp, "/opt/dotnet/dotnet", Workspace, "Console.WriteLine(1);");
        var values = CodeRunner.EnvironmentFor(CodeLanguage.CSharp, "/opt/dotnet/dotnet", Workspace);

        Assert.Equal("/bin/sh", command.FileName);
        Assert.Equal(["/opt/dotnet/dotnet", "/home/user/work/project/.corvid/code/dotnet"], command.Arguments[^2..]);
        Assert.Contains("/tmp/corvid-code", command.Arguments[1], StringComparison.Ordinal);
        Assert.Equal("/home/user/work/project/.corvid/code/dotnet/home", values["DOTNET_CLI_HOME"]);
        Assert.Equal("/opt/dotnet", values["DOTNET_ROOT"]);
        // Valid for the sandbox: no secret-like names, nothing the launcher sets itself.
        new SandboxPolicy { Workspace = Workspace, EnvironmentValues = values, Tools = new SandboxTools("/tmp/x/tools.sock", new string('a', 43)) }.Validate();
    }

    [Theory]
    [InlineData(50, 100, false)]
    [InlineData(1000, 100, true)]
    public void Long_output_keeps_its_start_and_mostly_its_end(int length, int max, bool cut)
    {
        var text = "HEAD" + new string('x', length) + "éTAIL";

        var (trimmed, wasCut) = CodeRunner.Trim(text, max);

        Assert.Equal(cut, wasCut);
        if (cut)
        {
            Assert.StartsWith("HEAD", trimmed, StringComparison.Ordinal);
            Assert.EndsWith("éTAIL", trimmed, StringComparison.Ordinal);
            Assert.Contains("bytes cut", trimmed, StringComparison.Ordinal);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(trimmed) < max + 40);
        }
        else
            Assert.Equal(text, trimmed);
    }
}

/// <summary>
/// A7: scripts under the real launcher and bubblewrap, speaking the wire format over the run's tools socket. Python runs
/// here, so CI requires these (python3 is on the runner); skipped only where bubblewrap cannot run or python3 is absent.
/// </summary>
[Trait("Category", "Confinement")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CodeConfinementTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _home;
    private readonly string _workspace;
    private readonly SqliteEventLog _log;
    private readonly ApprovalPipeline _pipeline;
    private readonly string _secret = "SECRET-" + RandomNumberGenerator.GetHexString(24, lowercase: true);

    public CodeConfinementTests()
    {
        _home = _tree.Path("home");
        _workspace = _tree.Path("home", "project");
        Directory.CreateDirectory(_workspace);
        _tree.Write("home/private/secret.txt", _secret);
        _tree.Write("home/project/data.txt", "workspace-data");
        _log = new SqliteEventLog(_tree.Path("events.db"));
        _pipeline = CodeFixtures.Pipeline(_log, ApprovalEnvironment.Create(_workspace, _home), new Answering(approve: true));
    }

    public void Dispose() => _tree.Dispose();

    internal static Dictionary<string, string> Environment(string home)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["LANG"] = "C.UTF-8",
        };
        if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
            environment["DOTNET_ROOT"] = dotnetRoot; // The framework-dependent launcher needs it to start.
        return environment;
    }

    private CodeTool Tool(CodeToolOptions? options = null, TimeSpan? timeout = null)
    {
        var sandbox = new BubblewrapSandbox(new SandboxOptions { Environment = Environment(_home), Timeout = timeout ?? TimeSpan.FromSeconds(60) });
        // The shell's denials are the script's: a directory the shell may not see.
        var shell = new SandboxedShell(sandbox, new ShellOptions
        {
            Workspace = _workspace,
            Environment = ["PATH", "HOME", "LANG"],
            DeniedPaths = [_tree.Path("home", "private")],
            Network = true,
        });
        options ??= new CodeToolOptions();
        return new CodeTool(shell, sandbox, options with { Runtimes = CodeRuntimes.Discover() }, () => CodeFixtures.Registry(_workspace));
    }

    private static void RequirePython() =>
        Assert.SkipWhen(CodeRuntimes.Discover().Python is null, "python3 is not installed, so the Python scripts cannot run.");

    private async Task<JsonElement> RunAsync(CodeTool tool, string code, ToolInvoker invoker, IReadOnlySet<string>? offered = null)
    {
        var context = new ToolCallContext(Fixtures.Session, "call-1")
        {
            Approval = await CodeFixtures.ApproveAsync(_pipeline, tool, "python", code),
            Invoker = invoker,
            OfferedTools = offered ?? new HashSet<string>(["read_file", "write_file", "web_fetch"], StringComparer.Ordinal),
        };
        return await Invoke(tool, CodeTool.ToolName, CodeFixtures.Arguments("python", code), context);
    }

    private static Task<NestedToolResult> Ok(string tool, int n, string result, bool tainted = false) =>
        Task.FromResult(new NestedToolResult($"call-1.{n}", Json(result), IsError: false, tainted));

    [Fact]
    public async Task A_script_calls_tools_through_the_turns_invoker_and_gets_their_results()
    {
        Host.RequireBubblewrap();
        RequirePython();
        var calls = new List<(string Tool, string Arguments)>();
        var code = CodeFixtures.PythonClient +
            """
            print(json.dumps(hello))
            print(json.dumps(call("read_file", {"path": "data.txt"})))
            print(json.dumps(call("shell", {"command": "rm -rf /"})))
            print(json.dumps(call("write_file", {"path": "x"})))
            print(open("data.txt").read())
            """;

        var result = await RunAsync(Tool(), code, (tool, arguments, _) =>
        {
            calls.Add((tool, arguments.GetRawText()));
            return tool == "read_file"
                ? Ok(tool, calls.Count, "\"file text\"")
                : Task.FromResult(new NestedToolResult($"call-1.{calls.Count}", Json("""{"error":"not_run","details":["denied by the user"]}"""), true, false));
        });

        Assert.Equal(0, result.GetProperty("exit_code").GetInt32());
        var lines = result.GetProperty("stdout").GetString()!.Split('\n');
        Assert.Contains("\"ok\": true", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"calls\": 50", lines[0], StringComparison.Ordinal);
        Assert.Equal("""{"id": 1, "ok": true, "result": "file text", "tainted": false}""", lines[1]);
        // A tool the turn does not offer the script (shell is not offered here) is refused by the socket; nothing ran.
        Assert.Contains("\"code\": \"unknown_tool\"", lines[2], StringComparison.Ordinal);
        // A refusal in the turn (a denied approval) comes back as tool_error with the kernel's error object.
        Assert.Contains("\"code\": \"tool_error\"", lines[3], StringComparison.Ordinal);
        Assert.Contains("denied by the user", lines[3], StringComparison.Ordinal);
        Assert.Equal("workspace-data", lines[4]);
        Assert.Equal([("read_file", """{"path": "data.txt"}"""), ("write_file", """{"path": "x"}""")], calls);
        var log = result.GetProperty("calls").EnumerateArray().ToList();
        Assert.Equal(2, result.GetProperty("tool_calls").GetInt32());
        Assert.Equal(["call-1.1", "call-1.2"], log.Select(call => call.GetProperty("call_id").GetString()));
        Assert.Equal([true, false], log.Select(call => call.GetProperty("ok").GetBoolean()));
    }

    [Fact]
    public async Task A_call_that_waits_for_its_approval_holds_the_script_until_it_is_answered()
    {
        Host.RequireBubblewrap();
        RequirePython();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<NestedToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var code = CodeFixtures.PythonClient + "print(json.dumps(call('write_file', {'path': 'out.txt', 'content': 'x'})))\nprint('after')";

        var run = RunAsync(Tool(), code, (_, _, token) =>
        {
            asked.TrySetResult();
            return answer.Task.WaitAsync(token);
        });
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(run.IsCompleted);
        answer.SetResult(new NestedToolResult("call-1.1", Json("""{"path":"out.txt","written":1}"""), false, false));
        var result = await run;

        var stdout = result.GetProperty("stdout").GetString()!;
        Assert.Contains("\"written\": 1", stdout, StringComparison.Ordinal);
        Assert.EndsWith("after\n", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tainting_result_reaches_the_script_marked_and_taints_the_run()
    {
        Host.RequireBubblewrap();
        RequirePython();
        var code = CodeFixtures.PythonClient + "r = call('web_fetch', {'url': 'https://example.com/'})\nprint(r['tainted'])";

        var result = await RunAsync(Tool(), code, (tool, _, _) => Ok(tool, 1, "\"<html>outside</html>\"", tainted: true));

        Assert.Equal("True\n", result.GetProperty("stdout").GetString());
        Assert.True(result.GetProperty("tainted").GetBoolean());
        Assert.True(Assert.Single(result.GetProperty("calls").EnumerateArray().ToList()).GetProperty("tainted").GetBoolean());
    }

    [Fact]
    public async Task The_call_budget_is_enforced_on_the_host()
    {
        Host.RequireBubblewrap();
        RequirePython();
        var code = CodeFixtures.PythonClient + "for i in range(4):\n    print(call('read_file', {'path': 'data.txt'}).get('error', {}).get('code', 'ok'))";
        var ran = 0;

        var result = await RunAsync(Tool(new CodeToolOptions { MaxCalls = 2 }), code, (tool, _, _) => Ok(tool, ++ran, "\"x\""));

        Assert.Equal("ok\nok\ncall_limit\ncall_limit\n", result.GetProperty("stdout").GetString());
        Assert.Equal(2, ran);
    }

    [Fact]
    public async Task Output_is_capped_on_the_host()
    {
        Host.RequireBubblewrap();
        RequirePython();
        const string code = "import sys\nprint('START' + 'o' * 20000 + 'END')\nsys.stderr.write('e' * 20000 + 'LAST')";

        var result = await RunAsync(Tool(new CodeToolOptions { MaxStdoutBytes = 2000, MaxStderrBytes = 1000 }), code, (_, _, _) => throw new InvalidOperationException());

        var stdout = result.GetProperty("stdout").GetString()!;
        var stderr = result.GetProperty("stderr").GetString()!;
        Assert.True(result.GetProperty("output_truncated").GetBoolean());
        Assert.StartsWith("START", stdout, StringComparison.Ordinal);
        Assert.EndsWith("END\n", stdout, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stdout) < 2100);
        Assert.EndsWith("LAST", stderr, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stderr) < 1100);
    }

    [Fact]
    public async Task A_script_that_runs_past_its_time_limit_is_stopped()
    {
        Host.RequireBubblewrap();
        RequirePython();
        const string code = "import time\nprint('started', flush=True)\nwhile True:\n    time.sleep(0.05)";

        var result = await RunAsync(Tool(new CodeToolOptions { Timeout = TimeSpan.FromSeconds(2) }, timeout: TimeSpan.FromSeconds(2)), code,
            (_, _, _) => throw new InvalidOperationException());

        Assert.True(result.GetProperty("timed_out").GetBoolean());
        Assert.Contains("time limit of 2 s", result.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("started\n", result.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task A_script_has_no_network_and_nothing_outside_the_workspace_but_reads()
    {
        Host.RequireBubblewrap();
        RequirePython();
        // A listener on the host's loopback: the sandbox has a network namespace of its own, so even this is out of reach.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var code = """
            import os, socket
            def attempt(label, f):
                try:
                    f(); print(label, "reached")
                except Exception as e:
                    print(label, "refused", type(e).__name__)
            attempt("loopback", lambda: socket.create_connection(("127.0.0.1", PORT), timeout=2))
            attempt("internet", lambda: socket.create_connection(("1.1.1.1", 443), timeout=2))
            attempt("dns", lambda: socket.getaddrinfo("example.com", 443))
            attempt("write-outside", lambda: open("../escaped.txt", "w").write("x"))
            attempt("write-inside", lambda: open("made.txt", "w").write("x"))
            token = open(os.environ["CORVID_TOOLS_TOKEN_FILE"]).read().strip()
            print("token-in-env", len(token) == 43 and any(token in v for v in os.environ.values()))
            attempt("read-denied", lambda: print(open("../private/secret.txt").read()))
            print("socket-dir", sorted(os.listdir("/run/corvid-sandbox")))
            """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var result = await RunAsync(Tool(), code, (_, _, _) => throw new InvalidOperationException());

        var stdout = result.GetProperty("stdout").GetString()!;
        Assert.Contains("loopback refused", stdout, StringComparison.Ordinal);
        Assert.Contains("internet refused", stdout, StringComparison.Ordinal);
        Assert.Contains("dns refused", stdout, StringComparison.Ordinal);
        Assert.Contains("write-outside refused", stdout, StringComparison.Ordinal);
        Assert.Contains("write-inside reached", stdout, StringComparison.Ordinal);
        Assert.Contains("read-denied refused", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(_secret, stdout + result.GetProperty("stderr").GetString(), StringComparison.Ordinal);
        Assert.Contains("token-in-env False", stdout, StringComparison.Ordinal);
        Assert.Contains("socket-dir ['tools.sock', 'tools.token']", stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(_tree.Path("home", "escaped.txt")));
        Assert.True(File.Exists(Path.Combine(_workspace, "made.txt")));
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_nothing_runs()
    {
        Host.RequireBubblewrap();
        RequirePython();
        const string code = """
            import json, os, socket
            s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            s.connect(os.environ["CORVID_TOOLS_SOCKET"])
            f = s.makefile("rwb")
            f.write(b'{"token":"' + b"A" * 43 + b'","v":1}\n'); f.flush()
            print(json.loads(f.readline())["error"]["code"])
            try:
                f.write(b'{"id":1,"tool":"read_file","args":{"path":"data.txt"}}\n'); f.flush()
                print(repr(f.readline()))
            except OSError:
                print("closed")
            """;

        var result = await RunAsync(Tool(), code, (_, _, _) => throw new InvalidOperationException("a call ran"));

        Assert.Matches("^unauthorized\n(b''|closed)\n$", result.GetProperty("stdout").GetString());
        Assert.Equal(0, result.GetProperty("tool_calls").GetInt32());
    }
}

/// <summary>
/// A7: the JavaScript and C# runners under the real sandbox. Not part of CI's pinned confinement count: each skips, saying
/// so, where its runtime is absent.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CodeLanguageTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _home;
    private readonly string _workspace;
    private readonly SqliteEventLog _log;
    private readonly ApprovalPipeline _pipeline;

    public CodeLanguageTests()
    {
        _home = _tree.Path("home");
        _workspace = _tree.Path("home", "project");
        Directory.CreateDirectory(_workspace);
        _tree.Write("home/project/data.txt", "workspace-data");
        _log = new SqliteEventLog(_tree.Path("events.db"));
        _pipeline = CodeFixtures.Pipeline(_log, ApprovalEnvironment.Create(_workspace, _home), new Answering(approve: true));
    }

    public void Dispose() => _tree.Dispose();

    private async Task<JsonElement> RunAsync(string language, string code, List<string> calls)
    {
        var sandbox = new BubblewrapSandbox(new SandboxOptions { Environment = CodeConfinementTests.Environment(_home), Timeout = TimeSpan.FromMinutes(3) });
        var shell = new SandboxedShell(sandbox, new ShellOptions { Workspace = _workspace, Environment = ["PATH", "HOME", "LANG"] });
        var tool = new CodeTool(shell, sandbox, new CodeToolOptions { Runtimes = CodeRuntimes.Discover() }, () => CodeFixtures.Registry(_workspace));
        var context = new ToolCallContext(Fixtures.Session, "call-1")
        {
            Approval = await CodeFixtures.ApproveAsync(_pipeline, tool, language, code),
            Invoker = (name, arguments, _) =>
            {
                calls.Add(name + " " + arguments.GetRawText());
                return Task.FromResult(new NestedToolResult("call-1." + calls.Count, Json("\"file text\""), false, false));
            },
            OfferedTools = new HashSet<string>(["read_file"], StringComparer.Ordinal),
        };
        return await Invoke(tool, CodeTool.ToolName, CodeFixtures.Arguments(language, code), context);
    }

    [Fact]
    public async Task A_javascript_module_calls_a_tool_over_the_socket()
    {
        Host.RequireBubblewrap();
        Assert.SkipWhen(CodeRuntimes.Discover().Node is null, "node is not installed, so JavaScript scripts cannot run.");
        const string code = """
            import net from 'node:net';
            import fs from 'node:fs';
            const socket = net.createConnection(process.env.CORVID_TOOLS_SOCKET);
            let buffer = '';
            const lines = [];
            let wake = null;
            socket.on('data', d => { buffer += d; let i; while ((i = buffer.indexOf('\n')) >= 0) { lines.push(buffer.slice(0, i)); buffer = buffer.slice(i + 1); } if (wake) wake(); });
            const next = async () => { while (!lines.length) await new Promise(r => wake = r); return JSON.parse(lines.shift()); };
            await new Promise(r => socket.once('connect', r));
            socket.write(JSON.stringify({ token: fs.readFileSync(process.env.CORVID_TOOLS_TOKEN_FILE, 'utf8').trim(), v: 1 }) + '\n');
            await next();
            socket.write(JSON.stringify({ id: 'a', tool: 'read_file', args: { path: 'data.txt' } }) + '\n');
            const reply = await next();
            console.log(reply.id, reply.ok, reply.result);
            socket.end();
            """;
        var calls = new List<string>();

        var result = await RunAsync("javascript", code, calls);

        Assert.Equal("a true file text\n", result.GetProperty("stdout").GetString());
        Assert.Equal(["read_file {\"path\":\"data.txt\"}"], calls);
    }

    [Fact]
    public async Task A_csharp_app_runs_outside_the_repositorys_build_files_and_calls_a_tool()
    {
        Host.RequireBubblewrap();
        Assert.SkipWhen(CodeRuntimes.Discover().Dotnet is null, "no .NET SDK was found, so C# scripts cannot run.");
        // A workspace whose build files would break any project built inside it.
        _tree.Write("home/project/Directory.Build.props", "<Project><Target Name=\"Poison\" BeforeTargets=\"Build\"><Error Text=\"POISONED\" /></Target></Project>");
        _tree.Write("home/project/Directory.Build.targets", "<Project><Target Name=\"Poison2\" BeforeTargets=\"Build\"><Error Text=\"POISONED\" /></Target></Project>");
        const string code = """
            using System.Net.Sockets;
            using System.Text.Json;
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(Environment.GetEnvironmentVariable("CORVID_TOOLS_SOCKET")!));
            using var stream = new NetworkStream(socket);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            var token = File.ReadAllText(Environment.GetEnvironmentVariable("CORVID_TOOLS_TOKEN_FILE")!).Trim();
            writer.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object> { ["token"] = token, ["v"] = 1 }));
            reader.ReadLine();
            writer.WriteLine("{\"id\":7,\"tool\":\"read_file\",\"args\":{\"path\":\"data.txt\"}}");
            using var reply = JsonDocument.Parse(reader.ReadLine()!);
            Console.WriteLine($"{reply.RootElement.GetProperty("id")} {reply.RootElement.GetProperty("result")} {File.ReadAllText("data.txt")}");
            """;
        var calls = new List<string>();

        var result = await RunAsync("csharp", code, calls);

        Assert.True(result.GetProperty("exit_code").GetInt32() == 0, result.GetProperty("stderr").GetString());
        Assert.Equal("7 file text workspace-data\n", result.GetProperty("stdout").GetString());
        Assert.Equal(["read_file {\"path\":\"data.txt\"}"], calls);
        // The SDK's caches stay in the workspace's .corvid, which keeps itself out of git.
        Assert.Equal("*\n", File.ReadAllText(Path.Combine(_workspace, ".corvid", "code", ".gitignore")));
        // A successful build says nothing: the script's own output is all there is.
        Assert.Equal("", result.GetProperty("stderr").GetString());

        // A second run in the workspace starts from the SDK's cached first use (no first-use step to make its directories).
        var again = await RunAsync("csharp", "Console.WriteLine(\"again\");", calls);
        Assert.True(again.GetProperty("exit_code").GetInt32() == 0, again.GetProperty("stderr").GetString());
        Assert.Equal("again\n", again.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task A_csharp_compile_error_is_reported_and_nothing_runs()
    {
        Host.RequireBubblewrap();
        Assert.SkipWhen(CodeRuntimes.Discover().Dotnet is null, "no .NET SDK was found, so C# scripts cannot run.");

        var result = await RunAsync("csharp", "Console.WriteLine(\"ran\")\nint x = \"not a number\";", []);

        Assert.NotEqual(0, result.GetProperty("exit_code").GetInt32());
        Assert.Contains("error CS", result.GetProperty("stderr").GetString(), StringComparison.Ordinal);
        Assert.Equal("", result.GetProperty("stdout").GetString());
    }
}
