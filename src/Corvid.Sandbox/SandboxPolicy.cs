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
    /// HTTPS through Corvid Vault's per-sandbox proxy (V-003b), the only route out: the network namespace stays private,
    /// 127.0.0.1:3128 inside relays to <see cref="SandboxProxy.SocketPath"/>, and the proxy and trust variables
    /// (<see cref="ProxyEnvironmentNames"/>) point at it and at the sandbox CA alone. Not with <see cref="Network"/>.
    /// </summary>
    public SandboxProxy? Proxy { get; init; }

    /// <summary>
    /// The variables the launcher sets for a proxied command. A policy with <see cref="Proxy"/> may not also pass any of
    /// these names through (compared without case); the launcher refuses them too.
    /// </summary>
    public static ImmutableArray<string> ProxyEnvironmentNames { get; } =
    [
        "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY", "no_proxy", "NO_PROXY", "SSL_CERT_FILE", "SSL_CERT_DIR",
        "CURL_CA_BUNDLE", "REQUESTS_CA_BUNDLE", "NODE_EXTRA_CA_CERTS", "NODE_USE_ENV_PROXY", "GIT_SSL_CAINFO", "PIP_CERT",
    ];

    /// <summary>
    /// A7: code mode's tools socket for this run (see <see cref="SandboxTools"/>): the socket alone is mounted read-only at
    /// <see cref="SandboxTools.SocketInside"/> and the token at <see cref="SandboxTools.TokenFileInside"/>, and the launcher
    /// sets <see cref="ToolsEnvironmentNames"/> to name them. It adds no network: combine it with neither <see cref="Network"/>
    /// nor <see cref="Proxy"/> for a script that must have none (code mode's own policy; the launcher allows both).
    /// </summary>
    public SandboxTools? Tools { get; init; }

    /// <summary>A7: the variables the launcher sets for a command with <see cref="Tools"/>; a policy may not also pass them.</summary>
    public static ImmutableArray<string> ToolsEnvironmentNames { get; } = ["CORVID_TOOLS_SOCKET", "CORVID_TOOLS_TOKEN_FILE"];

    /// <summary>
    /// A7: variables set for the command to these values (not read from Corvid's environment), for example a script
    /// runtime's settings. The name rules of <see cref="Environment"/> apply (secret-like names are refused), a name may not
    /// be in both, and a value travels on the launcher's command line, so it must never be a secret: secrets reach commands
    /// only through Corvid Vault, and the tools token through <see cref="Tools"/>.
    /// </summary>
    public ImmutableSortedDictionary<string, string> EnvironmentValues { get; init; } = ImmutableSortedDictionary<string, string>.Empty;

    /// <summary>The longest <see cref="EnvironmentValues"/> value the launcher accepts.</summary>
    public const int MaxEnvironmentValueChars = 32 * 1024;

    /// <summary>
    /// Names whose values pass from Corvid's environment to the command. Secret-like names (tokens, keys, passwords,
    /// agent sockets) are refused: secrets reach commands only through Corvid Vault (plan §10.5).
    /// </summary>
    public ImmutableArray<string> Environment { get; init; } = DefaultEnvironment;

    public static SandboxPolicy ForWorkspace(string workspace) => new() { Workspace = workspace };

    /// <summary>The launcher's rule: a name that looks like it carries a secret, which never passes to a command (§10.5).</summary>
    public static bool IsSecretLikeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return EnvironmentNames.IsSecretLike(name);
    }

    /// <summary>A portable environment variable name: a letter or '_', then letters, digits or '_'.</summary>
    public static bool IsValidName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return EnvironmentNames.IsValid(name);
    }

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

            if (Proxy is not null && ProxyEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{name} is set by the Vault proxy route; it cannot also be passed through.", nameof(Environment));
            }

            if (Tools is not null && ToolsEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{name} is set for the tools socket; it cannot also be passed through.", nameof(Environment));
            }
        }

        foreach (var (name, value) in EnvironmentValues)
        {
            if (!EnvironmentNames.IsValid(name))
            {
                throw new ArgumentException($"{name} is not a valid environment variable name.", nameof(EnvironmentValues));
            }

            if (EnvironmentNames.IsSecretLike(name))
            {
                throw new ArgumentException($"{name} looks like a secret. Secrets reach commands only through Corvid Vault.", nameof(EnvironmentValues));
            }

            if (Environment.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"{name} is both passed through and set to a value.", nameof(EnvironmentValues));
            }

            if (Proxy is not null && ProxyEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{name} is set by the Vault proxy route.", nameof(EnvironmentValues));
            }

            if (Tools is not null && ToolsEnvironmentNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{name} is set for the tools socket.", nameof(EnvironmentValues));
            }

            if (value is null || value.Length > MaxEnvironmentValueChars || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name}'s value is missing, longer than {MaxEnvironmentValueChars} characters or holds a NUL.", nameof(EnvironmentValues));
            }
        }

        if (Tools is { } tools)
        {
            Absolute(tools.SocketPath, nameof(SandboxTools.SocketPath));
            if (!SandboxTools.IsValidToken(tools.Token))
            {
                throw new ArgumentException("The tools token must be 32 to 256 characters of A-Z, a-z, 0-9, '-' or '_'.", nameof(Tools));
            }
        }

        if (Proxy is { } proxy)
        {
            if (Network)
            {
                throw new ArgumentException("A policy cannot have both Network and Proxy: the proxy is the only route out.", nameof(Proxy));
            }

            Absolute(proxy.SocketPath, nameof(SandboxProxy.SocketPath));
            if (string.IsNullOrEmpty(proxy.CaCertificatePem) || !proxy.CaCertificatePem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                throw new ArgumentException("The proxy's CA must be a PEM CERTIFICATE block.", nameof(Proxy));
            }

            if (proxy.CaCertificatePem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                throw new ArgumentException("The proxy's CA must not contain a private key.", nameof(Proxy));
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

/// <summary>Corvid Vault's per-sandbox proxy (V-003a's <c>sandbox_open</c>): its Unix socket and the CA certificate it presents.</summary>
/// <param name="SocketPath">The per-sandbox socket. Absolute.</param>
/// <param name="CaCertificatePem">The sandbox CA, PEM, public part only: the only certificate the command trusts.</param>
public sealed record SandboxProxy(string SocketPath, string CaCertificatePem);

/// <summary>
/// A7: code mode's tools socket for one run. Corvid listens on <paramref name="SocketPath"/> (a Unix socket in a directory
/// only this user can enter, outside the workspace, deleted after the run; put that directory in
/// <see cref="SandboxPolicy.DeniedPaths"/> when it is not under /tmp or /run, so its neighbours stay out of view). The
/// sandbox writes <paramref name="Token"/> to a 0600 file of its own and mounts it; the script reads it from
/// <c>$CORVID_TOOLS_TOKEN_FILE</c> and sends it first (docs/architecture.md, "Code mode wire format").
/// </summary>
/// <param name="SocketPath">The socket on the host. Absolute.</param>
/// <param name="Token">The run's token: 32 to 256 characters of A-Z, a-z, 0-9, '-' or '_'.</param>
public sealed record SandboxTools(string SocketPath, string Token)
{
    /// <summary>Where the socket appears inside the sandbox.</summary>
    public const string SocketInside = "/run/corvid-sandbox/tools.sock";

    /// <summary>Where the token file appears inside the sandbox.</summary>
    public const string TokenFileInside = "/run/corvid-sandbox/tools.token";

    /// <summary>A token the launcher and the wire accept: 32 to 256 URL-safe characters.</summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: >= 32 and <= 256 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The token is never printed with the record.</summary>
    public override string ToString() => $"SandboxTools {{ SocketPath = {SocketPath} }}";
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
