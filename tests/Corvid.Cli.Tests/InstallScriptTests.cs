using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Corvid.Cli.Tests;

/// <summary>
/// U-7, U-9: install.sh with and without <c>--app</c>, into a temporary prefix, from local tarballs in the release's layout
/// (stub binaries that print a version): the links, the app beside its own corvid-sandbox, the desktop entry and the icon,
/// and the refusals that leave the prefix untouched.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class InstallScriptTests : IDisposable
{
    private const string Version = "9.9.9";

    private readonly string _root = Directory.CreateTempSubdirectory("corvid-install-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Prefix => Path.Combine(_root, "prefix");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "install.sh")) && File.Exists(Path.Combine(directory.FullName, "Corvid.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("The repository root (install.sh beside Corvid.slnx) is not above the test's directory.");
    }

    private static void SkipUnlessReleasePlatform() =>
        Assert.SkipUnless(OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64,
            "install.sh installs Linux x86_64 releases only.");

    private static void Stub(string path, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"#!/bin/sh\necho '{output}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The CLI tarball as release.yml builds it: corvid, corvid-vault, corvid-sandbox and VERSION.</summary>
    private string CliTarball()
    {
        var stage = Path.Combine(_root, "stage-cli");
        Stub(Path.Combine(stage, "corvid"), $"corvid {Version}");
        Stub(Path.Combine(stage, "corvid-vault"), "corvid-vault");
        Stub(Path.Combine(stage, "corvid-sandbox"), "the native corvid-sandbox");
        File.WriteAllText(Path.Combine(stage, "VERSION"), Version + "\n");
        return Tar(stage, "corvid-linux-x64.tar.gz");
    }

    /// <summary>The app tarball as the CorvidAppTarball target builds it: the published app, its icons and VERSION.</summary>
    private string AppTarball(string version = Version, bool icon = true)
    {
        var stage = Path.Combine(_root, "stage-app-" + version + (icon ? "" : "-noicon"));
        Stub(Path.Combine(stage, "corvid-app"), "the app");
        File.WriteAllText(Path.Combine(stage, "corvid-app.dll"), "");
        File.WriteAllText(Path.Combine(stage, "VERSION"), version + "\n");
        if (icon)
        {
            var icons = Path.Combine(RepositoryRoot(), "src", "Corvid.App", "Assets", "Icons");
            Directory.CreateDirectory(Path.Combine(stage, "Assets", "Icons"));
            Directory.CreateDirectory(Path.Combine(stage, "Assets", "Icons", "Svg"));
            File.Copy(Path.Combine(icons, "Svg", "corvid.svg"), Path.Combine(stage, "Assets", "Icons", "Svg", "corvid.svg"));
            File.Copy(Path.Combine(icons, "corvid.png"), Path.Combine(stage, "Assets", "Icons", "corvid.png"));
        }
        return Tar(stage, Path.GetFileName(stage) + ".tar.gz");
    }

    private string Tar(string stage, string name)
    {
        var tarball = Path.Combine(_root, name);
        var (status, output) = Run("tar", ["-czf", tarball, "-C", stage, "."]);
        Assert.True(status == 0, output);
        return tarball;
    }

    private (int Status, string Output) Install(params string[] arguments) =>
        Run("sh", [Path.Combine(RepositoryRoot(), "install.sh"), .. arguments, "--prefix", Prefix]);

    private (int Status, string Output) Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        // A home of its own, so nothing reads or writes the person's; the system tools only on PATH.
        start.Environment["HOME"] = Path.Combine(_root, "home");
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment.Remove("CORVID_PREFIX");
        start.Environment.Remove("CORVID_RELEASE_URL");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.WaitForExitAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    [Fact]
    public void With_app_the_app_is_linked_beside_its_own_sandbox_with_a_desktop_entry_and_an_icon()
    {
        SkipUnlessReleasePlatform();

        var (status, output) = Install("--from", CliTarball(), "--app", "--from-app", AppTarball());

        Assert.True(status == 0, output);
        var dest = Path.Combine(Prefix, "lib", "corvid", Version);
        var bin = Path.Combine(Prefix, "bin");
        Assert.Equal(Path.Combine(dest, "app", "corvid-app"), new FileInfo(Path.Combine(bin, "corvid-app")).LinkTarget);
        Assert.Equal(Path.Combine(dest, "corvid"), new FileInfo(Path.Combine(bin, "corvid")).LinkTarget);
        // The app finds corvid-sandbox next to its real path: the release's native one, copied.
        Assert.Equal(File.ReadAllText(Path.Combine(dest, "corvid-sandbox")), File.ReadAllText(Path.Combine(dest, "app", "corvid-sandbox")));
        Assert.NotEqual(0, (int)(File.GetUnixFileMode(Path.Combine(dest, "app", "corvid-sandbox")) & UnixFileMode.UserExecute));
        var svg = Path.Combine(Prefix, "share", "icons", "hicolor", "scalable", "apps", "corvid-app.svg");
        Assert.True(File.Exists(svg));
        Assert.True(File.Exists(Path.Combine(Prefix, "share", "icons", "hicolor", "256x256", "apps", "corvid-app.png")));

        var desktop = File.ReadAllLines(Path.Combine(Prefix, "share", "applications", "corvid-app.desktop"));
        Assert.Equal("[Desktop Entry]", desktop[0]);
        Assert.Contains("Name=Corvid", desktop);
        Assert.Contains($"Exec=\"{Path.Combine(bin, "corvid-app")}\"", desktop);
        Assert.Contains($"Icon={svg}", desktop);
        Assert.Contains("Terminal=false", desktop);
        Assert.Contains("StartupWMClass=com.starlinghq.corvid", desktop);
        Assert.Contains("Installed the windowed app as", output, StringComparison.Ordinal);
        Assert.Contains("corvid 9.9.9", output, StringComparison.Ordinal);
        // Nothing outside the prefix: the home the script ran with is still empty.
        Assert.False(Directory.Exists(Path.Combine(_root, "home")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "home")).Any());

        if (Tool("desktop-file-validate") is { } validate)
        {
            var (valid, says) = Run(validate, [Path.Combine(Prefix, "share", "applications", "corvid-app.desktop")]);
            Assert.True(valid == 0, says);
        }
    }

    [Fact]
    public void Without_app_nothing_of_the_app_is_installed_and_the_script_says_how()
    {
        SkipUnlessReleasePlatform();

        var (status, output) = Install("--from", CliTarball());

        Assert.True(status == 0, output);
        Assert.True(File.Exists(Path.Combine(Prefix, "bin", "corvid")));
        Assert.False(File.Exists(Path.Combine(Prefix, "bin", "corvid-app")));
        Assert.False(Directory.Exists(Path.Combine(Prefix, "share", "applications")));
        Assert.False(Directory.Exists(Path.Combine(Prefix, "lib", "corvid", Version, "app")));
        Assert.Contains("install.sh --app", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0.0", true, "is version 1.0.0, not 9.9.9")]
    [InlineData(Version, false, "has no icon")]
    public void A_mismatched_or_incomplete_app_is_refused_before_anything_is_installed(string appVersion, bool icon, string says)
    {
        SkipUnlessReleasePlatform();

        var (status, output) = Install("--from", CliTarball(), "--from-app", AppTarball(appVersion, icon));

        Assert.NotEqual(0, status);
        Assert.Contains(says, output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Prefix));
    }

    [Fact]
    public void A_corvid_app_that_is_not_a_link_is_left_alone()
    {
        SkipUnlessReleasePlatform();
        Directory.CreateDirectory(Path.Combine(Prefix, "bin"));
        File.WriteAllText(Path.Combine(Prefix, "bin", "corvid-app"), "a hand-copied build");

        var (status, output) = Install("--from", CliTarball(), "--from-app", AppTarball());

        Assert.NotEqual(0, status);
        Assert.Contains("corvid-app exists and is not a link", output, StringComparison.Ordinal);
        Assert.Equal("a hand-copied build", File.ReadAllText(Path.Combine(Prefix, "bin", "corvid-app")));
        Assert.False(Directory.Exists(Path.Combine(Prefix, "lib")));
    }

    private static string? Tool(string program) =>
        new[] { "/usr/bin", "/usr/local/bin", "/bin" }.Select(directory => Path.Combine(directory, program)).FirstOrDefault(File.Exists);
}
