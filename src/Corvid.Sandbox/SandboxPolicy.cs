using System.Collections.Immutable;

namespace Corvid.Sandbox;

/// <summary>
/// What a sandboxed command may reach (plan §10.1). The launcher adds denials a policy cannot remove
/// (<see cref="BuiltInDenials"/>): ~/.ssh, ~/.gnupg, ~/.aws, ~/.config/gh, Corvid Vault's data and config directories,
/// the runtime directory, and .env files in the workspace and at the top of home.
/// </summary>
public sealed record SandboxPolicy
{
    /// <summary>Environment variables a command gets by default. Everything else is cleared.</summary>
    public static ImmutableArray<string> DefaultEnvironment { get; } =
        ["PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TZ"];

    /// <summary>The only directory the command can write (besides its own empty /tmp). Absolute.</summary>
    public required string Workspace { get; init; }

    /// <summary>Paths bound read-only, for example a part of the workspace that must not change. Absolute.</summary>
    public ImmutableArray<string> ReadOnlyPaths { get; init; } = [];

    /// <summary>Paths hidden in addition to the launcher's built-in denials. Absolute.</summary>
    public ImmutableArray<string> DeniedPaths { get; init; } = [];

    /// <summary>Share the host network. Off by default: the command gets a network namespace with only loopback.</summary>
    public bool Network { get; init; }

    /// <summary>
    /// Names whose values pass from Corvid's environment to the command. Secret-like names (tokens, keys, passwords,
    /// agent sockets) are refused: secrets reach commands only through Corvid Vault (plan §10.5).
    /// </summary>
    public ImmutableArray<string> Environment { get; init; } = DefaultEnvironment;

    public static SandboxPolicy ForWorkspace(string workspace) => new() { Workspace = workspace };

    /// <summary>Checks what can be checked without the file system. The launcher checks the rest and refuses.</summary>
    public void Validate()
    {
        Absolute(Workspace, nameof(Workspace));
        foreach (string path in ReadOnlyPaths)
        {
            Absolute(path, nameof(ReadOnlyPaths));
        }

        foreach (string path in DeniedPaths)
        {
            Absolute(path, nameof(DeniedPaths));
        }

        foreach (string name in Environment)
        {
            if (!EnvironmentNames.IsValid(name))
            {
                throw new ArgumentException($"{name} is not a valid environment variable name.", nameof(Environment));
            }

            if (EnvironmentNames.IsSecretLike(name))
            {
                throw new ArgumentException($"{name} looks like a secret. Secrets reach commands only through Corvid Vault.", nameof(Environment));
            }
        }
    }

    private static void Absolute(string path, string name)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{name}: {path} is not an absolute path.", name);
        }
    }
}

/// <summary>
/// The launcher's rule for environment names, repeated here so a secret-like name is refused before any value leaves
/// Corvid's process. The launcher's copy is authoritative; a test keeps the two in step.
/// </summary>
internal static class EnvironmentNames
{
    private static readonly string[] SecretMarkers =
        ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "CREDENTIAL", "PRIVATE", "AUTH", "COOKIE", "SESSION", "APIKEY"];

    private static readonly string[] SecretPrefixes =
        ["AWS_", "AZURE_", "GOOGLE_", "GCP_", "GH_", "GITHUB_", "GITLAB_", "OPENAI_", "ANTHROPIC_", "HF_", "SSH_", "GPG_", "GNUPG",
         "DBUS_", "KRB5", "VAULT_", "CORVID_VAULT", "DOCKER_", "KUBECONFIG", "NPM_CONFIG_", "PGPASS", "MYSQL_PWD"];

    public static bool IsValid(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static bool IsSecretLike(string name)
    {
        string n = name.ToUpperInvariant();
        return n.EndsWith("KEY", StringComparison.Ordinal) || n.Contains("_KEY_", StringComparison.Ordinal)
            || SecretMarkers.Any(m => n.Contains(m, StringComparison.Ordinal))
            || SecretPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }
}
