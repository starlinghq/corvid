using System.Security.Cryptography;
using Corvid.Approvals;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Sandbox;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

/// <summary>The shell tool's own refusals: it runs nothing without the approval pipeline's result for exactly this command.</summary>
public sealed class ShellToolTests : IDisposable
{
    private const string Workspace = "/home/user/work/project";

    private readonly TempTree _tree = new();
    private readonly SqliteEventLog _log;
    private readonly ApprovalPipeline _pipeline;
    private readonly RecordingSandbox _sandbox = new(command => new SandboxResult
    {
        ExitCode = 3,
        StandardOutput = new string('o', 50) + "END",
        StandardError = "warning",
        StandardOutputTruncated = true,
    });

    private readonly ShellTool _shell;

    public ShellToolTests()
    {
        _log = new SqliteEventLog(_tree.Path("events.db"));
        _pipeline = Pipeline(_log, ApprovalEnvironment.Create(Workspace, "/home/user"));
        _shell = new ShellTool(new SandboxedShell(_sandbox, new ShellOptions
        {
            Workspace = Workspace,
            Vault = new VaultLocations("/srv/vault", "/etc/vault.json", "/run/vault.sock"),
            DeniedPaths = ["/home/user/private"],
            MaxResultChars = 20,
        }));
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void The_tool_is_a_destructive_process_and_filesystem_side_effect()
    {
        var tool = Tool(_shell, "shell");

        Assert.Equal(new ToolRisk(SideEffects.Process | SideEffects.Filesystem, Network: false, Destructive: true), tool.Descriptor.Risk);
        Assert.Equal(["command"], tool.Descriptor.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task An_approved_command_runs_under_the_policy_and_long_output_is_cut_in_the_middle()
    {
        var context = await Approved(_pipeline, "ls -la");

        var result = await Invoke(_shell, "shell", """{"command":"ls -la"}""", context);

        var (policy, command) = Assert.Single(_sandbox.Runs);
        Assert.Equal(Workspace, policy.Workspace);
        Assert.False(policy.Network);
        Assert.Equal(["/srv/vault", "/etc/vault.json", "/run/vault.sock", "/home/user/private"], policy.DeniedPaths);
        Assert.Equal(new SandboxCommand("/bin/bash", "-c", "ls -la").Arguments, command.Arguments);
        Assert.Equal(3, result.GetProperty("exit_code").GetInt32());
        var stdout = result.GetProperty("stdout").GetString()!;
        Assert.StartsWith("ooooo\n… [", stdout, StringComparison.Ordinal);
        Assert.EndsWith("END", stdout, StringComparison.Ordinal);
        Assert.Equal("warning", result.GetProperty("stderr").GetString());
        Assert.True(result.GetProperty("output_truncated").GetBoolean());
    }

    [Fact]
    public async Task Without_an_approval_for_exactly_this_command_nothing_runs()
    {
        // Outside a turn: no context at all.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(_shell, "shell", """{"command":"ls"}"""));
        // A context with no approval.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(_shell, "shell", """{"command":"ls"}""", new ToolCallContext(Session, "c-1")));
        // An approval for another command.
        var other = await Approved(_pipeline, "ls");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(_shell, "shell", """{"command":"rm -rf build"}""", other));
        // A denial.
        var denied = await Approved(_pipeline, "cat ~/.ssh/id_ed25519 > leak.txt");
        Assert.False(((ApprovalResult)denied.Approval!).Allowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(_shell, "shell", """{"command":"cat ~/.ssh/id_ed25519 > leak.txt"}""", denied));
        // An approval of the call as an opaque tool call rather than as a parsed shell command.
        var opaque = new ApprovalPipeline(_pipeline.Environment, DegradedFabric(_log), _log, new Answering(approve: true));
        var asTool = await opaque.AuthorizeAsync(new ApprovalContext(Session, ApprovalMode.Smart, State()),
            new ToolAction(Tool(_shell, "shell").Descriptor, Json("""{"command":"ls"}""")), Ct);
        Assert.True(asTool.Allowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Invoke(_shell, "shell", """{"command":"ls"}""", new ToolCallContext(Session, "c-2") { Approval = asTool }));

        Assert.Empty(_sandbox.Runs);
    }

    [Fact]
    public void Vault_locations_default_to_vaults_own_and_extend_the_hardline_list()
    {
        var defaults = VaultLocations.Default("/home/user", name => name switch
        {
            "XDG_DATA_HOME" => "/data",
            "XDG_RUNTIME_DIR" => "/run/user/1000",
            "XDG_CONFIG_HOME" => "relative/ignored",
            _ => null,
        });
        Assert.Equal(["/data/corvid-vault", "/home/user/.config/corvid-vault/vault.json", "/run/user/1000/corvid-vault/vault.sock"], defaults.Paths);
        Assert.Null(VaultLocations.Default("/home/user", _ => null).SocketPath);

        var custom = new VaultLocations("/srv/vault", "/etc/vault.json");
        var environment = custom.Protect(ApprovalEnvironment.Create(Workspace, "/home/user"));
        Assert.Contains("/srv/vault", environment.Protected.Vault);
        Assert.Contains("/home/user/.local/share/corvid-vault", environment.Protected.Vault);
        Assert.Equal(Workspace, environment.Workspace);
        Assert.Throws<ArgumentException>(() => new VaultLocations("relative", "/etc/vault.json"));
    }
}

/// <summary>
/// The shell tool and the scheduler's script runner with the real launcher and bubblewrap. Skipped only where bubblewrap
/// cannot run; CI installs it and fails the build unless every one of these ran.
/// </summary>
[Trait("Category", "Confinement")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ShellConfinementTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _home;
    private readonly string _workspace;
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly VaultLocations _vault;
    private readonly SqliteEventLog _log;

    public ShellConfinementTests()
    {
        _home = _tree.Path("home");
        _workspace = _tree.Path("home", "project");
        Directory.CreateDirectory(_workspace);
        // Vault started with non-default locations, outside the directories the launcher hides by itself.
        _vault = new VaultLocations(_tree.Path("srv", "vault-data"), _tree.Path("etc", "vault-policy.json"), _tree.Path("run", "vault.sock"));
        Secret(Path.Combine(_vault.DataDirectory, "store.bin"));
        Secret(_vault.ConfigPath);
        Secret(_vault.SocketPath!);
        Secret(Path.Combine(_home, ".ssh", "id_ed25519"));
        _tree.Write("home/notes.txt", "visible-note");
        _tree.Write("home/project/README.md", "readme");
        _log = new SqliteEventLog(_tree.Path("events.db"));
    }

    public void Dispose() => _tree.Dispose();

    private void Secret(string path)
    {
        var value = "SECRET-" + RandomNumberGenerator.GetHexString(24, lowercase: true);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
        _secrets[path] = value;
    }

    private SandboxedShell Shell(TimeSpan? timeout = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = _home,
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["LANG"] = "C.UTF-8",
        };
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
            environment["DOTNET_ROOT"] = dotnetRoot; // The framework-dependent launcher needs it to start.
        var sandbox = new BubblewrapSandbox(new SandboxOptions { Environment = environment, Timeout = timeout ?? TimeSpan.FromSeconds(60) });
        return new SandboxedShell(sandbox, new ShellOptions { Workspace = _workspace, Vault = _vault });
    }

    private ApprovalPipeline Pipeline(IApprovalPrompter? prompter = null) =>
        Fixtures.Pipeline(_log, _vault.Protect(ApprovalEnvironment.Create(_workspace, _home)), prompter);

    [Fact]
    public async Task Commands_run_in_the_workspace_and_cannot_reach_vaults_configured_locations()
    {
        Host.RequireBubblewrap();
        var shell = new ShellTool(Shell());
        // The reads are in a script file the approval pipeline cannot see into (the hardline list would refuse them as a
        // command line), approved by a person as the turn loop would get it: the sandbox alone must stop them.
        var script = "cat ../notes.txt\necho made > made.txt\necho\n" + string.Join("\n", _secrets.Keys.Select(path => $"cat '{path}'"))
            + $"\nls -la '{_vault.DataDirectory}'\necho x >> ../notes.txt\necho done\n";
        _tree.Write("home/project/probe.sh", script);
        var context = await Approved(Pipeline(new Answering(approve: true)), "bash probe.sh");
        Assert.True(((ApprovalResult)context.Approval!).Allowed);

        var result = await Invoke(shell, "shell", """{"command":"bash probe.sh"}""", context);

        var stdout = result.GetProperty("stdout").GetString()!;
        var stderr = result.GetProperty("stderr").GetString()!;
        Assert.StartsWith("visible-note", stdout, StringComparison.Ordinal);
        Assert.EndsWith("done\n", stdout, StringComparison.Ordinal);
        Assert.Equal("made\n", File.ReadAllText(Path.Combine(_workspace, "made.txt")));
        Assert.Equal("visible-note", File.ReadAllText(Path.Combine(_home, "notes.txt")));
        foreach (var (path, secret) in _secrets)
        {
            Assert.DoesNotContain(secret, stdout + stderr, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("store.bin", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_have_no_network()
    {
        Host.RequireBubblewrap();
        var shell = new ShellTool(Shell());
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        // Reading a device always asks; a person approves it here, so only the sandbox stands in the way.
        var connect = $"cat < /dev/tcp/127.0.0.1/{port}";
        var interfaces = "grep -c : /proc/net/dev";

        var connected = await Invoke(shell, "shell", $$"""{"command":"{{connect}}"}""", await Approved(Pipeline(new Answering(approve: true)), connect));
        var listed = await Invoke(shell, "shell", $$"""{"command":"{{interfaces}}"}""", await Approved(Pipeline(), interfaces));

        Assert.NotEqual(0, connected.GetProperty("exit_code").GetInt32());
        Assert.False(listener.Pending());
        Assert.Equal("1", listed.GetProperty("stdout").GetString()!.Trim()); // loopback only
    }

    [Fact]
    public async Task Hardline_commands_are_refused_before_the_sandbox()
    {
        Host.RequireBubblewrap();
        var prompter = new Answering(approve: true);
        var context = await Approved(Pipeline(prompter), $"cat '{Path.Combine(_vault.DataDirectory, "store.bin")}'");

        var approval = (ApprovalResult)context.Approval!;
        Assert.False(approval.Allowed);
        Assert.Equal(ApprovalResolution.Hardline, approval.Resolution);
        Assert.Empty(prompter.Prompts);
    }

    [Fact]
    public async Task Pre_run_scripts_run_sandboxed_only_when_the_pipeline_allows_them_unattended()
    {
        Host.RequireBubblewrap();
        var runner = new SandboxedScriptRunner(Shell(), Pipeline(new Answering(approve: true)));

        var readOnly = await runner.RunAsync(new JobScriptRequest("job:a:1", "a", "ls; cat README.md", State()), Ct);
        Assert.True(readOnly.Ran);
        Assert.Equal(0, readOnly.ExitCode);
        Assert.Contains("README.md", readOnly.Output, StringComparison.Ordinal);
        Assert.Contains("readme", readOnly.Output, StringComparison.Ordinal);

        // A side effect would ask, and nobody can answer a scheduled run: refused without running.
        var writes = await runner.RunAsync(new JobScriptRequest("job:a:2", "a", "touch created-by-script", State()), Ct);
        Assert.False(writes.Ran);
        Assert.Contains("nobody can answer", writes.Refusal, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace, "created-by-script")));

        var vault = await runner.RunAsync(new JobScriptRequest("job:a:3", "a", $"cat '{_vault.ConfigPath}'", State()), Ct);
        Assert.False(vault.Ran);
        Assert.Contains("hardline", vault.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeouts_are_reported()
    {
        Host.RequireBubblewrap();
        var runner = new SandboxedScriptRunner(Shell(TimeSpan.FromSeconds(2)), Pipeline());

        var result = await runner.RunAsync(new JobScriptRequest("job:b:1", "b", "sleep 30", State()), Ct);

        Assert.True(result.Ran);
        Assert.Contains("[timed out]", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_script_runner_needs_the_shells_workspace()
    {
        var other = Fixtures.Pipeline(_log, ApprovalEnvironment.Create("/elsewhere", _home));
        Assert.Throws<ArgumentException>(() => new SandboxedScriptRunner(Shell(), other));
    }
}
