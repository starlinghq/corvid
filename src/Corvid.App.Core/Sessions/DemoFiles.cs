namespace Corvid.App.Core.Sessions;

/// <summary>
/// The files the demo scenario's edits touch, before and after, so a diff drawn from them reads as the mockups do: the
/// sandbox test of Review.png (lines 41 to 55) and the launcher check of the second approval in Approval.png.
/// </summary>
public static class DemoFiles
{
    public const string SeccompTestsPath = "tests/Corvid.Sandbox.Tests/SeccompTests.cs";

    public const string BubblewrapPath = "src/Corvid.Sandbox/BubblewrapSandbox.cs";

    public const string ReleaseNotesPath = "docs/releases/v0.2.md";

    private const string SeccompHead = """
        using System.Diagnostics;
        using Corvid.Sandbox;

        namespace Corvid.Sandbox.Tests;

        /// <summary>
        /// The sandbox's seccomp filter and namespaces, checked against the host: each test runs a control step outside the
        /// sandbox first, so a refusal inside means the sandbox refused and not the host.
        /// </summary>
        public sealed class SeccompTests : IAsyncLifetime
        {
            private BubblewrapSandbox Sandbox { get; set; } = null!;

            // The sysctl that turns unprivileged user namespaces off on Debian hosts.
            private const string UsernsSysctl = "/proc/sys/kernel/unprivileged_userns_clone";

            public async ValueTask InitializeAsync() => Sandbox = await BubblewrapSandbox.StartAsync(new SandboxOptions());

            public async ValueTask DisposeAsync() => await Sandbox.DisposeAsync();

            [Fact]
            public async Task Ptrace_is_refused_inside()
            {
                var inside = await Sandbox.RunAsync("strace -f true");
                Assert.NotEqual(0, inside.ExitCode);
            }

            [Fact]
            public async Task Mount_is_refused_inside()
            {
                var inside = await Sandbox.RunAsync("mount -t tmpfs none /mnt");
                Assert.NotEqual(0, inside.ExitCode);
            }

            [Fact]
            public async Task Keyctl_is_refused_inside()
            {
                var inside = await Sandbox.RunAsync("keyctl show");
                Assert.Contains("Operation not permitted", inside.StandardError);
            }

        """;

    /// <summary>SeccompTests.cs before turn 12's patch: the control step asserts unconditionally (lines 41 to 51).</summary>
    public static readonly string SeccompTestsBefore = SeccompHead + """
            [Fact]
            public async Task Unshare_user_namespace_is_refused_inside()
            {
                // Control: the host itself can create a user namespace.
                var control = await Run("unshare", "-U", "true");
                Assert.Equal(0, control.ExitCode);

                var inside = await Sandbox.RunAsync("unshare -U true");
                Assert.NotEqual(0, inside.ExitCode);
                Assert.Contains("Operation not permitted", inside.StandardError);
            }

            private static async Task<(int ExitCode, string StandardError)> Run(string file, params string[] arguments)
            {
                using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardError = true })!;
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, error);
            }
        }

        """;

    /// <summary>SeccompTests.cs after it: the control step allows a host whose sysctl forbids user namespaces (lines 41 to 55).</summary>
    public static readonly string SeccompTestsAfter = SeccompHead + """
            [Fact]
            public async Task Unshare_user_namespace_is_refused_inside()
            {
                // Control: the host itself can create a user namespace, unless the sysctl forbids it.
                var control = await Run("unshare", "-U", "true");
                Assert.True(control.ExitCode == 0 || UserNamespacesAreOff(), control.StandardError);

                var inside = await Sandbox.RunAsync("unshare -U true");
                Assert.NotEqual(0, inside.ExitCode);
                Assert.Contains("Operation not permitted", inside.StandardError);
            }

            private static bool UserNamespacesAreOff() =>
                File.Exists("/proc/sys/kernel/unprivileged_userns_clone")
                && File.ReadAllText("/proc/sys/kernel/unprivileged_userns_clone").Trim() == "0";

            private static async Task<(int ExitCode, string StandardError)> Run(string file, params string[] arguments)
            {
                using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardError = true })!;
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, error);
            }
        }

        """;

    /// <summary>The launcher's preflight before approval a-33's patch.</summary>
    public static readonly string BubblewrapBefore = """
        namespace Corvid.Sandbox;

        public sealed partial class BubblewrapSandbox
        {
            /// <summary>What the host must allow before a sandbox starts; each failure names its fix.</summary>
            internal static IReadOnlyList<string> Preflight(HostFacts host)
            {
                var problems = new List<string>();
                if (!host.HasBubblewrap)
                    problems.Add("bubblewrap is not installed: install the bubblewrap package");
                if (!host.SeccompAvailable)
                    problems.Add("seccomp is unavailable: the kernel was built without CONFIG_SECCOMP_FILTER");
                return problems;
            }
        }

        """;

    /// <summary>The launcher's preflight after a-33: it says when unprivileged user namespaces are off.</summary>
    public static readonly string BubblewrapAfter = """
        namespace Corvid.Sandbox;

        public sealed partial class BubblewrapSandbox
        {
            /// <summary>What the host must allow before a sandbox starts; each failure names its fix.</summary>
            internal static IReadOnlyList<string> Preflight(HostFacts host)
            {
                var problems = new List<string>();
                if (!host.HasBubblewrap)
                    problems.Add("bubblewrap is not installed: install the bubblewrap package");
                if (!host.SeccompAvailable)
                    problems.Add("seccomp is unavailable: the kernel was built without CONFIG_SECCOMP_FILTER");
                if (host.UnprivilegedUserNamespaces == false)
                    problems.Add("unprivileged user namespaces are off: set kernel.unprivileged_userns_clone=1, or run the setuid bwrap");
                return problems;
            }
        }

        """;

    /// <summary>The release notes the second session is writing.</summary>
    public static readonly string ReleaseNotes = """
        # Corvid 0.2

        ## What is new

        - The windowed app's first screens, on Uno Platform.
        - `corvid mcp serve` exposes read-only project memory.
        - Detection rules run inline at pre-execute and over the log with `corvid scan`.

        ## Fixes

        - The sandbox's control step no longer fails on hosts without unprivileged user namespaces.

        """;
}
