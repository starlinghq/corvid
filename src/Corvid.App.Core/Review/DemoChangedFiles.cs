using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Review;

/// <summary>
/// The texts of the files the demo's main session changed, before and after, with the call and checkpoint that cover each:
/// what Review draws its diffs from and what Rewind previews a restore with. The sandbox files are the Sessions demo's own
/// (<see cref="DemoFiles"/>); ci.yml's three added lines are this area's.
/// </summary>
internal static class DemoChangedFiles
{
    public const string CiPath = ".github/workflows/ci.yml";

    private static readonly string CiBefore = """
        name: ci
        on: [push, pull_request]
        jobs:
          build:
            runs-on: [self-hosted, arch]
            steps:
              - uses: actions/checkout@v4
              - run: dotnet build Corvid.slnx -c Release
              - run: dotnet test --solution Corvid.slnx -c Release

        """;

    private static readonly string CiAfter = """
        name: ci
        on: [push, pull_request]
        jobs:
          build:
            runs-on: [self-hosted, arch]
            steps:
              - uses: actions/checkout@v4
              - run: dotnet build Corvid.slnx -c Release
              - run: dotnet test --solution Corvid.slnx -c Release
                env:
                  CI: "true"
                  CORVID_SANDBOX_STRICT: "1"

        """;

    // The launcher as turn 11's patch (c-40 to c-41) found and left it: the session counts that patch +19 −5, so Review's
    // diff draws those lines. The Sessions demo's two-line a-33 patch (DemoFiles) is the preflight's last step of it.
    private static readonly string BubblewrapBefore = """
        namespace Corvid.Sandbox;

        public sealed partial class BubblewrapSandbox
        {
            /// <summary>What the host must allow before a sandbox starts.</summary>
            internal static IReadOnlyList<string> Preflight(HostFacts host)
            {
                var problems = new List<string>();
                if (!host.HasBubblewrap)
                    problems.Add("bubblewrap is not installed");
                if (!host.SeccompAvailable)
                    problems.Add("seccomp is unavailable");
                // User namespaces are not checked yet: bwrap fails later with
                // "No permissions to create new namespace".
                return problems;
            }
        }

        """;

    private static readonly string BubblewrapAfter = """
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

            /// <summary>Whether unprivileged user namespaces are on; null when the kernel does not say.</summary>
            internal static bool? ReadUserNamespaces(string root = "/")
            {
                var path = Path.Combine(root, "proc/sys/kernel/unprivileged_userns_clone");
                if (!File.Exists(path))
                    return null;
                return File.ReadAllText(path).Trim() switch
                {
                    "1" => true,
                    "0" => false,
                    _ => null,
                };
            }
        }

        """;

    /// <summary>What the demo knows of each path: its texts and the call and checkpoint that cover it.</summary>
    public static readonly Dictionary<string, (string? Old, string New, string Tool, int Turn, string Checkpoint)> Known = new(StringComparer.Ordinal)
    {
        [DemoFiles.BubblewrapPath] = (BubblewrapBefore, BubblewrapAfter, "patch_file", 11, "c-41"),
        [DemoFiles.SeccompTestsPath] = (DemoFiles.SeccompTestsBefore, DemoFiles.SeccompTestsAfter, "patch_file", 12, "c-44"),
        [CiPath] = (CiBefore, CiAfter, "write_file", 10, "c-41"),
        [DemoFiles.ReleaseNotesPath] = (null, DemoFiles.ReleaseNotes, "write_file", 1, "c-12"),
    };

}
