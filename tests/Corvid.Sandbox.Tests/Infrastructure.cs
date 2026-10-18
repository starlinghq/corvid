using System.Diagnostics;
using System.Security.Cryptography;
using Corvid.Sandbox.Launcher;

namespace Corvid.Sandbox.Tests;

/// <summary>Whether this machine can run the confinement tests, and why not.</summary>
internal static class Host
{
    private static readonly Lazy<string?> BubblewrapProblem = new(ProbeBubblewrap);

    public static string LauncherPath => Path.Combine(AppContext.BaseDirectory, "corvid-sandbox");

    /// <summary>Skips the calling test unless bubblewrap can create every namespace the launcher asks for.</summary>
    public static void RequireBubblewrap()
    {
        Assert.SkipWhen(BubblewrapProblem.Value is not null, BubblewrapProblem.Value ?? "");
    }

    public static void RequireLandlock()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Landlock.Abi() > 0, "The kernel has no Landlock (needs 5.13+ with landlock in lsm=).");
    }

    private static string? ProbeBubblewrap()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Linux only.";
        }

        string? bwrap = new[] { "/usr/bin/bwrap", "/bin/bwrap", "/usr/local/bin/bwrap" }.FirstOrDefault(File.Exists);
        if (bwrap is null)
        {
            return "bubblewrap is not installed.";
        }

        var start = new ProcessStartInfo(bwrap) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in (string[])["--unshare-all", "--unshare-user", "--disable-userns", "--ro-bind", "/", "/", "--", "/bin/true"])
        {
            start.ArgumentList.Add(a);
        }

        using Process probe = Process.Start(start)!;
        string error = probe.StandardError.ReadToEnd();
        probe.WaitForExit();
        return probe.ExitCode == 0 ? null : $"bubblewrap cannot create namespaces here (exit {probe.ExitCode}): {error.Trim()}";
    }
}

/// <summary>
/// A throwaway home with secrets in every place the launcher must hide, a workspace inside it, and a plain file that
/// must stay readable (so a hidden secret is hidden by the sandbox, not by the layout). It lives under the test's
/// output directory, never under /tmp: the sandbox replaces /tmp, which would hide a layout there for the wrong reason.
/// </summary>
internal sealed class SandboxLayout : IDisposable
{
    public SandboxLayout(bool customXdg = false)
    {
        Root = Path.Combine(AppContext.BaseDirectory, "confinement", RandomNumberGenerator.GetHexString(12, lowercase: true));
        Assert.False(Root.StartsWith("/tmp/", StringComparison.Ordinal) || Root.StartsWith("/run/", StringComparison.Ordinal),
            $"The layout must be visible inside the sandbox; {Root} would be replaced by its empty /tmp or /run.");
        Home = Path.Combine(Root, "home");
        Workspace = Path.Combine(Home, "project");
        Outside = Path.Combine(Root, "outside");
        string dataHome = customXdg ? Path.Combine(Root, "xdg-data") : Path.Combine(Home, ".local", "share");
        string configHome = customXdg ? Path.Combine(Root, "xdg-config") : Path.Combine(Home, ".config");

        Secret(Path.Combine(Home, ".ssh", "id_ed25519"));
        Secret(Path.Combine(Home, ".ssh", "config"));
        Secret(Path.Combine(Home, ".gnupg", "private-keys-v1.d", "key.key"));
        Secret(Path.Combine(Home, ".aws", "credentials"));
        Secret(Path.Combine(Home, ".config", "gh", "hosts.yml"));
        Secret(Path.Combine(Home, ".env"));
        Secret(Path.Combine(dataHome, "corvid-vault", "store.bin"));
        Secret(Path.Combine(dataHome, "corvid-vault", "master.cred"));
        Secret(Path.Combine(configHome, "corvid-vault", "vault.json"));
        Secret(Path.Combine(Workspace, ".env"));
        Secret(Path.Combine(Workspace, "service", "deep", ".env.production"));
        VaultData = Path.Combine(dataHome, "corvid-vault");
        VaultConfig = Path.Combine(configHome, "corvid-vault");

        VisibleCanary = "visible-" + RandomNumberGenerator.GetHexString(16, lowercase: true);
        Write(Path.Combine(Home, "notes.txt"), VisibleCanary);
        Write(Path.Combine(Workspace, "README.md"), VisibleCanary);
        Directory.CreateDirectory(Outside);

        Environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
            ["LANG"] = "C.UTF-8",
            // Present in Corvid's environment but not allowed through: neither may reach the command.
            ["PLAIN_SETTING"] = "plain-" + RandomNumberGenerator.GetHexString(16, lowercase: true),
            ["GITHUB_TOKEN"] = "ghp_" + RandomNumberGenerator.GetHexString(36, lowercase: true),
        };
        if (System.Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
        {
            Environment["DOTNET_ROOT"] = dotnetRoot; // The framework-dependent launcher needs it to start.
        }

        if (customXdg)
        {
            Environment["XDG_DATA_HOME"] = dataHome;
            Environment["XDG_CONFIG_HOME"] = configHome;
        }
    }

    public string Root { get; }

    public string Home { get; }

    public string Workspace { get; }

    public string Outside { get; }

    public string VaultData { get; }

    public string VaultConfig { get; }

    /// <summary>Secret file path to its contents. None of these may appear in anything a sandboxed command prints.</summary>
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    public string VisibleCanary { get; }

    public Dictionary<string, string> Environment { get; }

    public SandboxPolicy Policy => SandboxPolicy.ForWorkspace(Workspace);

    public BubblewrapSandbox Sandbox(SandboxOptions? options = null) =>
        new((options ?? new SandboxOptions()) with { Environment = Environment, Timeout = options?.Timeout ?? TimeSpan.FromSeconds(60) });

    /// <summary>Starts corvid-sandbox directly with this layout's environment.</summary>
    public ProcessStartInfo Launcher(params string[] arguments)
    {
        var start = new ProcessStartInfo(Host.LauncherPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (var (name, value) in Environment)
        {
            start.Environment[name] = value;
        }

        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        return start;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the output directory is disposable.
        }
    }

    private void Secret(string path)
    {
        string value = "SECRET-" + RandomNumberGenerator.GetHexString(24, lowercase: true);
        Write(path, value);
        Secrets[path] = value;
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}
