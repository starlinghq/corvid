using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Corvid.Sandbox.Tests;

/// <summary>
/// Real commands under the real launcher and bubblewrap. Each test checks a positive control too (a plain file is
/// readable, the workspace is writable, output comes back), so a denial is the sandbox's doing and not an artefact of
/// the layout. CI runs this class by trait and fails if any test skipped.
/// </summary>
[Trait("Category", "Confinement")]
public sealed class ConfinementTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SandboxLayout _layout = new();

    public void Dispose() => _layout.Dispose();

    [Fact]
    public async Task Output_reaches_the_watcher_while_the_command_runs_and_the_result_is_unchanged()
    {
        Host.RequireBubblewrap();
        var pieces = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // K-2: the command waits for a file the test writes only once "first" has arrived, so it arrived while it ran.
        string script = """
            echo first
            while [ ! -f go ]; do sleep 0.05; done
            echo warning >&2
            printf 'caf\303\251\n'
            """;
        var command = SandboxCommand.Shell(script) with
        {
            Output = piece =>
            {
                pieces.Enqueue(piece);
                if (string.Concat(pieces).Contains("first\n", StringComparison.Ordinal))
                    first.TrySetResult();
            },
        };

        Task<SandboxResult> running = _layout.Sandbox().RunAsync(_layout.Policy, command, Ct);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(running.IsCompleted);
        await File.WriteAllTextAsync(Path.Combine(_layout.Workspace, "go"), "", Ct);
        SandboxResult result = await running;

        string watched = string.Concat(pieces);
        Assert.Contains("first\n", watched, StringComparison.Ordinal);
        Assert.Contains("warning\n", watched, StringComparison.Ordinal);
        // A character is decoded whole, however the pipe split it.
        Assert.Contains("café\n", watched, StringComparison.Ordinal);
        Assert.Equal("first\ncafé\n", result.StandardOutput);
        Assert.Equal("warning\n", result.StandardError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Secrets_and_Vault_directories_cannot_be_read_or_listed(bool customXdg)
    {
        Host.RequireBubblewrap();
        using var layout = new SandboxLayout(customXdg);
        string reads = string.Join('\n', layout.Secrets.Keys.Select(path => $"cat '{path}'"));
        string listings = string.Join('\n', layout.Secrets.Keys.Select(Path.GetDirectoryName).Distinct().Select(dir => $"ls -la '{dir}'"));
        string script = $"""
            cat '{Path.Combine(layout.Home, "notes.txt")}'
            {reads}
            {listings}
            ls -la '{layout.VaultData}/' '{layout.VaultConfig}/'
            find '{layout.Home}' '{layout.Root}'
            grep -r SECRET- '{layout.Root}'
            echo done
            """;

        SandboxResult result = await layout.Sandbox().RunAsync(layout.Policy, SandboxCommand.Shell(script), Ct);
        string everything = result.StandardOutput + result.StandardError;

        Assert.Contains(layout.VisibleCanary, result.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith("done\n", result.StandardOutput, StringComparison.Ordinal);
        foreach (var (path, secret) in layout.Secrets)
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
            Assert.True(Denied(result.StandardError, "cat", path), $"Reading {path} was not refused:\n{result.StandardError}");
        }

        // Directory listings show nothing inside: not a file name, let alone a value. (The cat errors name the files
        // the script itself asked for.)
        string shown = result.StandardOutput + string.Join('\n', result.StandardError.Split('\n').Where(l => !l.StartsWith("cat:", StringComparison.Ordinal)));
        foreach (string name in (string[])["id_ed25519", "key.key", "credentials", "hosts.yml", "store.bin", "master.cred", "vault.json"])
        {
            Assert.DoesNotContain(name, shown, StringComparison.Ordinal);
        }

        Assert.True(Denied(result.StandardError, "ls", layout.VaultData), $"Listing {layout.VaultData} was not refused.");
        Assert.True(Denied(result.StandardError, "ls", layout.VaultConfig), $"Listing {layout.VaultConfig} was not refused.");
        Assert.Contains("bubblewrap", result.Layers);
    }

    [Fact]
    public async Task Nothing_outside_the_workspace_can_be_written()
    {
        Host.RequireBubblewrap();
        string[] outside =
        [
            Path.Combine(_layout.Home, "new-in-home"),
            Path.Combine(_layout.Outside, "new-outside"),
            Path.Combine(_layout.Home, "notes.txt"),
            Path.Combine(_layout.Workspace, ".env"),
        ];
        string script = string.Join('\n', outside.Select(path => $"echo x >> '{path}' || echo 'refused {path}'"))
            + $"\nmkdir '{Path.Combine(_layout.Home, "new-dir")}' || echo refused-mkdir"
            + $"\nrm -f '{Path.Combine(_layout.Home, "notes.txt")}' || echo refused-rm"
            + "\necho made > made-in-workspace && mkdir -p sub/dir && echo nested > sub/dir/file && echo wrote-workspace"
            + "\necho tmp > /tmp/scratch && cat /tmp/scratch";

        SandboxResult result = await _layout.Sandbox().RunAsync(_layout.Policy, SandboxCommand.Shell(script), Ct);

        foreach (string path in outside)
        {
            Assert.Contains($"refused {path}", result.StandardOutput, StringComparison.Ordinal);
        }

        Assert.Contains("refused-mkdir", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("refused-rm", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_layout.Home, "new-in-home")));
        Assert.False(File.Exists(Path.Combine(_layout.Outside, "new-outside")));
        Assert.False(Directory.Exists(Path.Combine(_layout.Home, "new-dir")));
        Assert.Equal(_layout.VisibleCanary, File.ReadAllText(Path.Combine(_layout.Home, "notes.txt")));
        Assert.Equal(_layout.Secrets[Path.Combine(_layout.Workspace, ".env")], File.ReadAllText(Path.Combine(_layout.Workspace, ".env")));

        // The controls: the workspace is writable, and /tmp is the sandbox's own.
        Assert.Contains("wrote-workspace", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("made\n", File.ReadAllText(Path.Combine(_layout.Workspace, "made-in-workspace")));
        Assert.Equal("nested\n", File.ReadAllText(Path.Combine(_layout.Workspace, "sub", "dir", "file")));
        Assert.Contains("tmp", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_only_paths_inside_the_workspace_stay_read_only()
    {
        Host.RequireBubblewrap();
        string locked = Path.Combine(_layout.Workspace, "locked");
        Directory.CreateDirectory(locked);
        SandboxPolicy policy = _layout.Policy with { ReadOnlyPaths = [locked] };

        SandboxResult result = await _layout.Sandbox().RunAsync(
            policy, SandboxCommand.Shell("touch locked/x || echo refused; touch free && echo wrote"), Ct);

        Assert.Contains("refused", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("wrote", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(locked, "x")));
    }

    [Fact]
    public async Task Extra_denied_paths_are_hidden()
    {
        Host.RequireBubblewrap();
        string secretDir = Path.Combine(_layout.Root, "company-secrets");
        Directory.CreateDirectory(secretDir);
        File.WriteAllText(Path.Combine(secretDir, "plan.txt"), "classified-" + _layout.VisibleCanary);
        SandboxPolicy policy = _layout.Policy with { DeniedPaths = [secretDir] };

        SandboxResult result = await _layout.Sandbox().RunAsync(policy, SandboxCommand.Shell($"ls '{secretDir}'; cat '{secretDir}/plan.txt'"), Ct);

        Assert.DoesNotContain("plan.txt", result.StandardOutput + result.StandardError.Replace($"{secretDir}/plan.txt", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("classified-", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_network_unless_the_policy_allows_it()
    {
        Host.RequireBubblewrap();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string script = $"if (exec 3<>/dev/tcp/127.0.0.1/{port}); then echo connected; else echo no-route; fi; grep -c : /proc/net/dev";
        var bash = new SandboxCommand("/bin/bash", "-c", script);

        SandboxResult offline = await _layout.Sandbox().RunAsync(_layout.Policy, bash, Ct);
        SandboxResult online = await _layout.Sandbox().RunAsync(_layout.Policy with { Network = true }, bash, Ct);

        Assert.Contains("no-route", offline.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("1", offline.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]); // loopback only
        Assert.Contains("connected", online.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_environment_is_cleared_except_the_allowlist()
    {
        Host.RequireBubblewrap();

        SandboxResult result = await _layout.Sandbox().RunAsync(_layout.Policy, new SandboxCommand("/usr/bin/env"), Ct);

        var names = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line[..line.IndexOf('=', StringComparison.Ordinal)]).ToHashSet();
        Assert.Superset(new HashSet<string>(["HOME", "PATH", "LANG"]), names);
        Assert.Subset(new HashSet<string>(SandboxPolicy.DefaultEnvironment), names);
        Assert.DoesNotContain(_layout.Environment["PLAIN_SETTING"], result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(_layout.Environment["GITHUB_TOKEN"], result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"HOME={_layout.Home}\n", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_environment_name_is_refused_before_anything_runs()
    {
        Host.RequireBubblewrap();
        SandboxPolicy policy = _layout.Policy with { Environment = ["PATH", "GITHUB_TOKEN"] };

        await Assert.ThrowsAsync<ArgumentException>(() => _layout.Sandbox().RunAsync(policy, new SandboxCommand("/bin/true"), Ct));

        // The launcher refuses it too, for callers that are not Corvid.Sandbox (Vault).
        string marker = Path.Combine(_layout.Workspace, "ran");
        using Process launcher = Process.Start(_layout.Launcher("--workspace", _layout.Workspace, "--env", "GITHUB_TOKEN", "--", "/usr/bin/touch", marker))!;
        string stderr = await launcher.StandardError.ReadToEndAsync(Ct);
        await launcher.WaitForExitAsync(Ct);
        Assert.Equal(125, launcher.ExitCode);
        Assert.Contains("looks like a secret", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task The_command_runs_in_new_namespaces_and_cannot_make_its_own()
    {
        Host.RequireBubblewrap();
        string script = """
            echo "pid1=$(tr '\0' '\n' < /proc/1/cmdline | head -n 1)"
            echo "pids=$(ls /proc | grep -c '^[0-9]')"
            command -v unshare > /dev/null && echo has-unshare
            unshare -U true && echo nested-userns
            unshare -r true && echo nested-root
            """;

        SandboxResult result = await _layout.Sandbox().RunAsync(_layout.Policy, SandboxCommand.Shell(script), Ct);

        Assert.Matches("pid1=\\S*bwrap\n", result.StandardOutput);
        Assert.Contains("has-unshare", result.StandardOutput, StringComparison.Ordinal);
        int pids = int.Parse(result.StandardOutput.Split('\n').Single(l => l.StartsWith("pids=", StringComparison.Ordinal))[5..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(pids, 1, 10); // bubblewrap's init, the shell, and what the shell started; not the host's processes
        Assert.DoesNotContain("nested-", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_codes_input_and_layers_come_back()
    {
        Host.RequireBubblewrap();

        SandboxResult result = await _layout.Sandbox().RunAsync(
            _layout.Policy, SandboxCommand.Shell("cat; echo err >&2; exit 7") with { StandardInput = "from stdin" }, Ct);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("from stdin", result.StandardOutput);
        Assert.Equal("err\n", result.StandardError);
        Assert.False(result.TimedOut);
        Assert.Contains("bubblewrap", result.Layers);
        Assert.Contains("seccomp", result.Layers);
        Assert.Contains(result.Layers, l => l.StartsWith("landlock-v", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_command_is_reported_as_127_not_as_a_refusal()
    {
        Host.RequireBubblewrap();

        SandboxResult result = await _layout.Sandbox().RunAsync(_layout.Policy, new SandboxCommand("no-such-command-anywhere"), Ct);

        Assert.Equal(127, result.ExitCode);
        Assert.Contains("command not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_beyond_the_limit_is_dropped_and_flagged()
    {
        Host.RequireBubblewrap();

        SandboxResult result = await _layout.Sandbox(new SandboxOptions { MaxOutputBytes = 1000 }).RunAsync(
            _layout.Policy, SandboxCommand.Shell("head -c 200000 /dev/zero | tr '\\0' a; echo tail >&2"), Ct);

        Assert.Equal(1000, result.StandardOutput.Length);
        Assert.True(result.StandardOutputTruncated);
        Assert.Equal("tail\n", result.StandardError);
        Assert.False(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task A_timeout_kills_the_command_and_everything_it_started()
    {
        Host.RequireBubblewrap();
        string marker = "123.4" + Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();

        SandboxResult result = await _layout.Sandbox(new SandboxOptions { Timeout = TimeSpan.FromSeconds(2) }).RunAsync(
            _layout.Policy, SandboxCommand.Shell($"sleep {marker} & sleep {marker}"), Ct);

        Assert.True(result.TimedOut);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(30));
        await AssertGoneAsync(marker);
    }

    [Fact]
    public async Task Cancellation_kills_the_command()
    {
        Host.RequireBubblewrap();
        string marker = "124.4" + Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _layout.Sandbox().RunAsync(_layout.Policy, SandboxCommand.Shell($"sleep {marker}"), cancel.Token));

        await AssertGoneAsync(marker);
    }

    [Fact]
    public async Task The_sandbox_dies_with_the_launcher()
    {
        Host.RequireBubblewrap();
        string marker = "125.4" + Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using Process launcher = Process.Start(_layout.Launcher("--workspace", _layout.Workspace, "--", "/bin/sleep", marker))!;

        await WaitForAsync(() => SleepPids(marker).Count > 0, "the sandboxed sleep to start");
        launcher.Kill(); // SIGKILL to the launcher alone, not its tree: --die-with-parent must do the rest.
        await launcher.WaitForExitAsync(Ct);

        await AssertGoneAsync(marker);
    }

    [Fact]
    public async Task Missing_bubblewrap_is_refused_and_nothing_runs()
    {
        string marker = Path.Combine(_layout.Workspace, "ran");

        var e = await Assert.ThrowsAsync<SandboxRefusedException>(() => _layout.Sandbox(new SandboxOptions { BubblewrapPath = "/nonexistent/bwrap" })
            .RunAsync(_layout.Policy, new SandboxCommand("/usr/bin/touch", marker), Ct));

        Assert.Contains("bubblewrap not found", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_stand_in_for_bubblewrap_that_creates_no_namespaces_is_refused()
    {
        string fake = Path.Combine(_layout.Root, "fake-bwrap");
        string dotnetRoot = _layout.Environment.TryGetValue("DOTNET_ROOT", out string? root) ? $"export DOTNET_ROOT='{root}'\n" : "";
        File.WriteAllText(fake, $"#!/bin/sh\n{dotnetRoot}while [ \"$1\" != -- ]; do shift; done\nshift\nexec \"$@\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string marker = Path.Combine(_layout.Workspace, "ran");

        var e = await Assert.ThrowsAsync<SandboxRefusedException>(() => _layout.Sandbox(new SandboxOptions { BubblewrapPath = fake })
            .RunAsync(_layout.Policy, new SandboxCommand("/usr/bin/touch", marker), Ct));

        Assert.Contains("namespace", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_bubblewrap_that_fails_to_set_up_is_refused()
    {
        string marker = Path.Combine(_layout.Workspace, "ran");

        var e = await Assert.ThrowsAsync<SandboxRefusedException>(() => _layout.Sandbox(new SandboxOptions { BubblewrapPath = "/bin/false" })
            .RunAsync(_layout.Policy, new SandboxCommand("/usr/bin/touch", marker), Ct));

        Assert.Contains("could not set up the sandbox", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_launcher_inside_the_workspace_is_refused()
    {
        Host.RequireBubblewrap();
        // The command could replace it, and the next run would execute the replacement unconfined.
        SandboxPolicy policy = SandboxPolicy.ForWorkspace(AppContext.BaseDirectory.TrimEnd('/'));

        var e = await Assert.ThrowsAsync<SandboxRefusedException>(() => _layout.Sandbox().RunAsync(policy, new SandboxCommand("/bin/true"), Ct));

        Assert.Contains("inside the workspace", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_workspace_inside_a_denied_path_is_refused()
    {
        Host.RequireBubblewrap();
        string inside = Path.Combine(_layout.Home, ".ssh", "project");
        Directory.CreateDirectory(inside);

        var e = await Assert.ThrowsAsync<SandboxRefusedException>(
            () => _layout.Sandbox().RunAsync(SandboxPolicy.ForWorkspace(inside), new SandboxCommand("/bin/true"), Ct));

        Assert.Contains("inside the denied path", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_file_inside_the_workspace_is_refused()
    {
        Host.RequireBubblewrap();
        // The command could swap the status file for a symlink, and the unconfined launcher would then write through it.
        string target = Path.Combine(_layout.Outside, "precious");
        File.WriteAllText(target, _layout.VisibleCanary);
        string status = Path.Combine(_layout.Workspace, "status.json");
        string marker = Path.Combine(_layout.Workspace, "ran");

        var (exitCode, stderr) = await LaunchAsync(
            "--workspace", _layout.Workspace, "--status-file", status, "--",
            "/bin/sh", "-c", $"touch '{marker}'; ln -sf '{target}' '{status}'");

        Assert.Equal(125, exitCode);
        Assert.Contains("must be outside the workspace", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.Equal(_layout.VisibleCanary, File.ReadAllText(target));
    }

    [Fact]
    public async Task A_symlinked_status_file_is_not_followed()
    {
        string target = Path.Combine(_layout.Outside, "precious");
        File.WriteAllText(target, _layout.VisibleCanary);
        string status = Path.Combine(_layout.Root, "status.json");
        File.CreateSymbolicLink(status, target);
        string marker = Path.Combine(_layout.Workspace, "ran");

        var (exitCode, stderr) = await LaunchAsync("--workspace", _layout.Workspace, "--status-file", status, "--", "/usr/bin/touch", marker);

        Assert.Equal(125, exitCode);
        Assert.Contains("must not be a symlink", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.Equal(_layout.VisibleCanary, File.ReadAllText(target));
    }

    [Fact]
    public async Task A_missing_launcher_is_refused()
    {
        var e = await Assert.ThrowsAsync<SandboxRefusedException>(() => new BubblewrapSandbox(new SandboxOptions { LauncherPath = "/nonexistent/corvid-sandbox" })
            .RunAsync(_layout.Policy, new SandboxCommand("/bin/true"), Ct));

        Assert.Contains("refusing to run unsandboxed", e.Message, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Stderr)> LaunchAsync(params string[] arguments)
    {
        using Process launcher = Process.Start(_layout.Launcher(arguments))!;
        Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(Ct);
        string stderr = await launcher.StandardError.ReadToEndAsync(Ct);
        await launcher.WaitForExitAsync(Ct);
        await stdout;
        return (launcher.ExitCode, stderr);
    }

    /// <summary>A "Permission denied" message from the tool about the path (coreutils quote paths in some versions).</summary>
    private static bool Denied(string stderr, string tool, string path) =>
        stderr.Split('\n').Any(line => line.StartsWith(tool + ":", StringComparison.Ordinal)
            && line.Contains(path, StringComparison.Ordinal) && line.EndsWith("Permission denied", StringComparison.Ordinal));

    private static List<int> SleepPids(string marker)
    {
        var pids = new List<int>();
        foreach (string dir in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(dir), out int pid))
            {
                try
                {
                    string cmdline = File.ReadAllText(Path.Combine(dir, "cmdline"));
                    if (cmdline.StartsWith("sleep\0", StringComparison.Ordinal) || cmdline.StartsWith("/bin/sleep\0", StringComparison.Ordinal) || cmdline.StartsWith("/usr/bin/sleep\0", StringComparison.Ordinal))
                    {
                        if (cmdline.Contains("\0" + marker, StringComparison.Ordinal))
                        {
                            pids.Add(pid);
                        }
                    }
                }
                catch (IOException)
                {
                    // Exited while we looked.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return pids;
    }

    private static Task AssertGoneAsync(string marker) => WaitForAsync(() => SleepPids(marker).Count == 0, $"sleep {marker} to be killed");

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Timed out waiting for {what}.");
            await Task.Delay(50, Ct);
        }
    }
}
