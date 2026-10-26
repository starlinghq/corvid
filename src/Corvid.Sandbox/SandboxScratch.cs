using System.Security.Cryptography;
using System.Text;

namespace Corvid.Sandbox;

/// <summary>
/// A7 review (A7V1-2): where a run keeps what only it may see on the host: the sandbox's status directory (its status file,
/// the proxy's CA, code mode's tools token) and code mode's tools socket. Each is a directory of its own, mode 0700, under one
/// root that every sandbox hides from the command it runs, so a command in another sandbox cannot list or read them whatever
/// <c>TMPDIR</c> is:
/// <list type="bullet">
/// <item><c>$XDG_RUNTIME_DIR/corvid-runs</c> when the runtime directory is set: the launcher hides the whole runtime directory
/// from every command (and replaces <c>/run</c>, where it usually is).</item>
/// <item>Otherwise <c>corvid-runs-&lt;user&gt;</c> in the temporary directory. Under <c>/tmp</c> the sandbox's own empty <c>/tmp</c>
/// covers it; anywhere else (<c>TMPDIR</c> on CI) <see cref="DeniedRoot"/> names it, and every policy this assembly launches
/// denies it.</item>
/// </list>
/// The root is made 0700 and set to 0700 again on each use: changing its mode fails unless this user owns it, so a root another
/// user made first is refused rather than used.
/// </summary>
public static class SandboxScratch
{
    /// <summary>The root's name under the runtime directory.</summary>
    public const string RuntimeRootName = "corvid-runs";

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>The root for this environment (see the class); not created.</summary>
    /// <param name="environment">The variables read (<c>XDG_RUNTIME_DIR</c>, <c>TMPDIR</c>); null reads this process's.</param>
    public static string Root(IReadOnlyDictionary<string, string>? environment = null)
    {
        string? Get(string name) => environment is null ? Environment.GetEnvironmentVariable(name)
            : environment.TryGetValue(name, out var value) ? value : null;
        if (Get("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Path.IsPathFullyQualified(runtime) && Directory.Exists(runtime))
            return Path.Combine(runtime, RuntimeRootName);
        var temp = Get("TMPDIR") is { Length: > 0 } tmp && Path.IsPathFullyQualified(tmp) ? tmp : "/tmp";
        return Path.Combine(Path.TrimEndingDirectorySeparator(temp), RuntimeRootName + "-" + User());
    }

    /// <summary>
    /// The root a policy must deny so other sandboxes cannot see into it: <see cref="Root"/> unless the sandbox replaces it
    /// anyway (under <c>/tmp</c> or <c>/run</c>, or in the runtime directory, which the launcher hides); null then.
    /// </summary>
    public static string? DeniedRoot(IReadOnlyDictionary<string, string>? environment = null)
    {
        var root = Root(environment);
        return IsUnder(root, "/tmp") || IsUnder(root, "/run") || Path.GetFileName(root) == RuntimeRootName ? null : root;
    }

    /// <summary>A new directory, mode 0700, named <paramref name="prefix"/> and random hex, under <see cref="Root"/>.</summary>
    public static DirectoryInfo CreateDirectory(string prefix, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The sandbox runs on Linux only.");
        var root = EnsureRoot(environment);
        for (var attempt = 0; ; attempt++)
        {
            // Short: a Unix socket path holds 107 bytes, and code mode's socket is made in one of these.
            var path = Path.Combine(root, prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
            if (Directory.Exists(path) && attempt < 8)
                continue;
            return Directory.CreateDirectory(path, Private);
        }
    }

    /// <summary>
    /// <see cref="Root"/>, made (0700, this user's) if missing. A command launched to run for long (an MCP server) is given
    /// the root to deny before any run has made it, so it must exist then: the launcher hides only what exists.
    /// </summary>
    public static string EnsureRoot(IReadOnlyDictionary<string, string>? environment = null)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The sandbox runs on Linux only.");
        var root = Root(environment);
        try
        {
            Directory.CreateDirectory(root, Private);
            // Fails unless this user owns it: a root someone else made is never used.
            File.SetUnixFileMode(root, Private);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            throw new SandboxRefusedException($"The sandbox's private directory {root} is not this user's own ({exception.Message}).", exception);
        }

        if (File.GetUnixFileMode(root) != Private || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new SandboxRefusedException($"The sandbox's private directory {root} is not a mode-0700 directory of this user's.");
        return root;
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(directory + "/", StringComparison.Ordinal);

    // The user's name in a directory name: letters, digits, '-' and '_' only.
    private static string User()
    {
        var name = new StringBuilder();
        foreach (var c in Environment.UserName)
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return name.Length > 0 ? name.ToString() : "user";
    }
}
