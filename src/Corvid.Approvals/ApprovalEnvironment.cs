using System.Collections.Immutable;

namespace Corvid.Approvals;

/// <summary>
/// Paths the hardline list protects (plan §10.2 step 3). Vault's directories are denied to every access; credential
/// stores and Corvid's safety configuration are denied to writes, and reading a credential store always asks.
/// </summary>
/// <param name="Vault">
/// Vault's data, configuration and runtime directories (<c>vault.md</c>: <c>$XDG_DATA_HOME/corvid-vault</c>,
/// <c>$XDG_CONFIG_HOME/corvid-vault</c>, <c>$XDG_RUNTIME_DIR/corvid-vault</c>).
/// </param>
/// <param name="CredentialStores">Directories and files that hold credentials (<c>~/.ssh</c>, <c>~/.aws</c>, <c>/etc/shadow</c>).</param>
/// <param name="SafetyConfiguration">Corvid's own approval and sandbox configuration (<c>$XDG_CONFIG_HOME/corvid</c>).</param>
public sealed record ProtectedPaths(
    ImmutableArray<string> Vault,
    ImmutableArray<string> CredentialStores,
    ImmutableArray<string> SafetyConfiguration)
{
    /// <summary>The defaults for a user's home and XDG directories.</summary>
    public static ProtectedPaths For(string home, string dataHome, string configHome, string? runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(configHome);
        var vault = new List<string> { Path.Combine(dataHome, "corvid-vault"), Path.Combine(configHome, "corvid-vault") };
        if (!string.IsNullOrWhiteSpace(runtimeDirectory))
            vault.Add(Path.Combine(runtimeDirectory, "corvid-vault"));
        string[] homeStores =
        [
            ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker/config.json", ".netrc", ".git-credentials", ".pgpass",
            ".npmrc", ".pypirc", ".password-store", ".config/gh", ".config/gcloud", ".local/share/keyrings",
        ];
        return new ProtectedPaths(
            [.. vault.Select(PathMath.Normalize)],
            [.. homeStores.Select(store => PathMath.Normalize(Path.Combine(home, store))),
                "/etc/shadow", "/etc/gshadow", "/etc/sudoers", "/etc/sudoers.d"],
            [PathMath.Normalize(Path.Combine(configHome, "corvid"))]);
    }

    /// <summary>
    /// File names that hold credentials wherever they are: <c>.env</c> and <c>.env.*</c> (except <c>.example</c>,
    /// <c>.sample</c> and <c>.template</c>), private keys and certificates with keys.
    /// </summary>
    public static bool IsCredentialFileName(string name)
    {
        if (name == ".env")
            return true;
        if (name.StartsWith(".env.", StringComparison.Ordinal))
            return !(name.EndsWith(".example", StringComparison.Ordinal) || name.EndsWith(".sample", StringComparison.Ordinal)
                || name.EndsWith(".template", StringComparison.Ordinal));
        return name.EndsWith(".pem", StringComparison.Ordinal) || name.EndsWith(".key", StringComparison.Ordinal)
            || name.EndsWith(".p12", StringComparison.Ordinal) || name.EndsWith(".pfx", StringComparison.Ordinal)
            || name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" or "id_ecdsa_sk" or "id_ed25519_sk";
    }
}

/// <summary>
/// What the approval pipeline knows about where commands run: the workspace, the home directory, the environment
/// variables it may resolve, and the protected paths. Everything is explicit so analysis is deterministic and testable.
/// </summary>
public sealed record ApprovalEnvironment
{
    public ApprovalEnvironment(string workspace, string home, ProtectedPaths protectedPaths,
        IEnumerable<KeyValuePair<string, string>>? variables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(protectedPaths);
        if (!Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(home))
            throw new ArgumentException("The workspace and home must be absolute paths.");
        Workspace = PathMath.Normalize(workspace);
        Home = PathMath.Normalize(home);
        Protected = protectedPaths;
        var known = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (variables is not null)
            foreach (var (name, value) in variables)
                known[name] = value;
        known["HOME"] = Home;
        Variables = known.ToImmutable();
    }

    public string Workspace { get; }

    public string Home { get; }

    public ProtectedPaths Protected { get; }

    /// <summary>Variables whose values commands may rely on (<c>$HOME</c>, the XDG directories). Others are unknown.</summary>
    public ImmutableDictionary<string, string> Variables { get; }

    /// <summary>A hermetic environment from explicit XDG directories, with the defaults the XDG specification gives.</summary>
    public static ApprovalEnvironment Create(string workspace, string home, string? dataHome = null, string? configHome = null,
        string? runtimeDirectory = null)
    {
        dataHome ??= Path.Combine(home, ".local", "share");
        configHome ??= Path.Combine(home, ".config");
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_DATA_HOME"] = dataHome,
            ["XDG_CONFIG_HOME"] = configHome,
        };
        if (runtimeDirectory is not null)
            variables["XDG_RUNTIME_DIR"] = runtimeDirectory;
        return new ApprovalEnvironment(workspace, home, ProtectedPaths.For(home, dataHome, configHome, runtimeDirectory), variables);
    }

    /// <summary>From this process's home and XDG variables.</summary>
    public static ApprovalEnvironment FromProcess(string workspace)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Create(Path.GetFullPath(workspace), home, Env("XDG_DATA_HOME"), Env("XDG_CONFIG_HOME"), Env("XDG_RUNTIME_DIR"));

        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }
}

/// <summary>Lexical path arithmetic on Unix paths.</summary>
internal static class PathMath
{
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="directory"/>.</summary>
    public static bool IsUnder(string path, string directory) =>
        directory == "/" ? path.Length > 1 && path[0] == '/' : path.StartsWith(directory + "/", StringComparison.Ordinal);

    public static bool IsAtOrUnder(string path, string directory) => path == directory || IsUnder(path, directory);

    public static string[] Components(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);
}
