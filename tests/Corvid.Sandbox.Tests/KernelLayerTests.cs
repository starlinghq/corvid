using System.Diagnostics;

namespace Corvid.Sandbox.Tests;

/// <summary>
/// Landlock and seccomp on their own: stage 2 run directly, outside bubblewrap, so a pass cannot be bubblewrap's
/// read-only mounts or --disable-userns doing the work.
/// </summary>
[Trait("Category", "Confinement")]
public sealed class KernelLayerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SandboxLayout _layout = new();

    public void Dispose() => _layout.Dispose();

    [Fact]
    public async Task Landlock_allows_writes_only_beneath_the_writable_paths()
    {
        Host.RequireLandlock();
        string allowed = Path.Combine(_layout.Workspace, "landlock-ok");
        string denied = Path.Combine(_layout.Outside, "landlock-denied");
        string script = $"echo a > '{allowed}' && echo wrote; echo b > '{denied}' || echo refused; cat '{Path.Combine(_layout.Home, "notes.txt")}'; echo c > /dev/null && echo devnull";

        var (exitCode, stdout, stderr) = await Stage2Async(["--rw", _layout.Workspace], "/bin/sh", "-c", script);

        Assert.Equal(0, exitCode);
        Assert.Contains("wrote", stdout, StringComparison.Ordinal);
        Assert.Contains("refused", stdout, StringComparison.Ordinal);
        Assert.Contains(_layout.VisibleCanary, stdout, StringComparison.Ordinal); // reads are still allowed
        Assert.Contains("devnull", stdout, StringComparison.Ordinal);
        Assert.Contains("Permission denied", stderr, StringComparison.Ordinal);
        Assert.True(File.Exists(allowed));
        Assert.False(File.Exists(denied));
    }

    [Fact]
    public async Task Seccomp_refuses_new_namespaces_and_mounts()
    {
        Host.RequireLandlock(); // stage 2 requires both layers unless told otherwise
        Host.RequireBubblewrap(); // only so that unprivileged user namespaces are known to work here (the control below)
        string script = "unshare -U true && echo nested-userns; unshare -r true && echo nested-root; echo end";

        // The control: the same commands succeed outside the sandbox.
        using (Process control = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "unshare -U true && echo control-ok"]) { RedirectStandardOutput = true })!)
        {
            string output = await control.StandardOutput.ReadToEndAsync(Ct);
            await control.WaitForExitAsync(Ct);
            Assert.Contains("control-ok", output, StringComparison.Ordinal);
        }

        var (_, stdout, stderr) = await Stage2Async(["--rw", _layout.Workspace], "/bin/sh", "-c", script);

        Assert.Contains("end", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-", stdout, StringComparison.Ordinal);
        Assert.Contains("Operation not permitted", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage_2_execs_with_exactly_the_given_environment()
    {
        Host.RequireLandlock();

        var (_, stdout, _) = await Stage2Async(["--rw", _layout.Workspace, "--env", "ONLY=this"], "/usr/bin/env");

        Assert.Equal("ONLY=this\n", stdout);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> Stage2Async(string[] options, params string[] command)
    {
        using Process stage2 = Process.Start(_layout.Launcher(["--stage2", .. options, "--", .. command]))!;
        Task<string> stdout = stage2.StandardOutput.ReadToEndAsync(Ct);
        Task<string> stderr = stage2.StandardError.ReadToEndAsync(Ct);
        await stage2.WaitForExitAsync(Ct);
        return (stage2.ExitCode, await stdout, await stderr);
    }
}
