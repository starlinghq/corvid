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

    /// <summary>What the demo knows of each path: its texts and the call and checkpoint that cover it.</summary>
    public static readonly Dictionary<string, (string? Old, string New, string Tool, int Turn, string Checkpoint)> Known = new(StringComparer.Ordinal)
    {
        [DemoFiles.BubblewrapPath] = (DemoFiles.BubblewrapBefore, DemoFiles.BubblewrapAfter, "patch_file", 11, "c-41"),
        [DemoFiles.SeccompTestsPath] = (DemoFiles.SeccompTestsBefore, DemoFiles.SeccompTestsAfter, "patch_file", 12, "c-44"),
        [CiPath] = (CiBefore, CiAfter, "write_file", 10, "c-41"),
        [DemoFiles.ReleaseNotesPath] = (null, DemoFiles.ReleaseNotes, "write_file", 1, "c-12"),
    };

}
