using System.Collections.Immutable;
using Corvid.Sandbox;

namespace Corvid.Approvals;

/// <summary>
/// Paths the hardline list protects (plan §10.2 step 3). Vault's directories are denied to every access; credential
/// stores and Corvid's safety configuration are denied to writes, and reading a credential store always asks.
/// </summary>
/// <param name="Vault">
/// Vault's data and configuration directories as the sandbox hides them (<see cref="BuiltInDenials.VaultDirectories"/>:
/// the defaults under home and the XDG locations), and its runtime directory, <c>$XDG_RUNTIME_DIR/corvid-vault</c>.
/// </param>
/// <param name="CredentialStores">
/// Directories and files that hold credentials: the sandbox's secret paths (<see cref="BuiltInDenials.SecretPaths"/>) and
/// runtime directory, plus stores the sandbox does not hide (<c>~/.kube</c>, <c>~/.netrc</c>, <c>/etc/shadow</c>, ...).
/// </param>
/// <param name="SafetyConfiguration">Corvid's own approval and sandbox configuration (<c>$XDG_CONFIG_HOME/corvid</c>).</param>
public sealed record ProtectedPaths(
    ImmutableArray<string> Vault,
    ImmutableArray<string> CredentialStores,
    ImmutableArray<string> SafetyConfiguration)
{
    // Credential stores in home beyond the sandbox's built-in denials. Writes are denied and reads ask; the sandbox
    // leaves them readable, so widening its list is a C-007a follow-up.
    private static readonly string[] OtherHomeStores =
    [
        ".azure", ".kube", ".docker/config.json", ".netrc", ".git-credentials", ".pgpass", ".npmrc", ".pypirc",
        ".password-store", ".config/gcloud", ".local/share/keyrings",
    ];

    /// <summary>The protected paths for a home directory and the XDG variables <paramref name="environment"/> reads.</summary>
    /// <param name="environment">Reads <c>XDG_DATA_HOME</c>, <c>XDG_CONFIG_HOME</c> and <c>XDG_RUNTIME_DIR</c>; null is unset.</param>
    public static ProtectedPaths For(string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);
        var vault = BuiltInDenials.VaultDirectories(home, environment).ToList();
        var stores = BuiltInDenials.SecretPaths(home, environment).Concat(OtherHomeStores.Select(store => Path.Combine(home, store))).ToList();
        if (BuiltInDenials.RuntimeDirectory(environment) is { } runtime)
        {
            vault.Add(Path.Combine(runtime, BuiltInDenials.VaultDirectoryName));
            // ssh-agent, gpg-agent and session-bus sockets; the sandbox hides the whole directory.
            stores.Add(runtime);
        }

        var configHome = environment("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathFullyQualified(config)
            ? config
            : Path.Combine(home, ".config");
        return new ProtectedPaths(
            [.. vault.Select(PathMath.Normalize).Distinct(StringComparer.Ordinal)],
            [.. stores.Select(PathMath.Normalize).Distinct(StringComparer.Ordinal), "/etc/shadow", "/etc/gshadow", "/etc/sudoers", "/etc/sudoers.d"],
            [PathMath.Normalize(Path.Combine(configHome, "corvid"))]);
    }

    /// <summary>
    /// File names that hold credentials wherever they are: the sandbox's <c>.env</c> and <c>.env.*</c>
    /// (<see cref="BuiltInDenials.IsEnvFileName"/>) except <c>.example</c>, <c>.sample</c> and <c>.template</c>, which a
    /// project may ship and an agent may write (the sandbox still hides existing ones); private keys and certificates
    /// with keys.
    /// </summary>
    public static bool IsCredentialFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (BuiltInDenials.IsEnvFileName(name))
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

    /// <summary>
    /// A hermetic environment from explicit XDG directories. Relative ones are ignored, as the XDG specification says.
    /// For expansion, an unset data or config directory takes its default, which is also protected whatever is set.
    /// </summary>
    public static ApprovalEnvironment Create(string workspace, string home, string? dataHome = null, string? configHome = null,
        string? runtimeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["XDG_DATA_HOME"] = Absolute(dataHome) ?? Path.Combine(home, ".local", "share"),
            ["XDG_CONFIG_HOME"] = Absolute(configHome) ?? Path.Combine(home, ".config"),
        };
        if (Absolute(runtimeDirectory) is { } runtime)
            variables["XDG_RUNTIME_DIR"] = runtime;
        return new ApprovalEnvironment(workspace, home, ProtectedPaths.For(home, name => variables.GetValueOrDefault(name)), variables);

        static string? Absolute(string? path) => path is { Length: > 0 } && Path.IsPathFullyQualified(path) ? path : null;
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
