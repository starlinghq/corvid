using System.Collections.Immutable;
using Corvid.Sandbox;
using Corvid.Vault.Client;

namespace Corvid.Mcp;

/// <summary>How Corvid reaches a configured MCP server.</summary>
public enum McpTransportKind
{
    /// <summary>A local process speaking MCP on its standard streams, started through the sandbox launcher.</summary>
    Stdio,

    /// <summary>Streamable HTTP; a credential goes through Vault's egress proxy, never through Corvid.</summary>
    Http,
}

/// <summary>
/// How far Corvid believes what a server says about itself (plan §10.3, §10.6). Nothing is trusted by default: a server's
/// annotations could claim a tool is read-only so that it runs unasked, and its results are content from outside the user.
/// </summary>
public enum McpTrust
{
    /// <summary>Annotations are ignored: every tool is a side effect that asks, and every result taints the session.</summary>
    Untrusted,

    /// <summary>The user vouches for the server: its read-only and destructive hints are honoured, and only open-world results taint.</summary>
    Trusted,
}

/// <summary>One configured MCP server, validated before anything starts (<see cref="Validate"/>).</summary>
public sealed record McpServerDefinition
{
    /// <summary>Lower-case letters, digits, '-' and '_', at most 24 characters; it prefixes the server's tool names.</summary>
    public required string Name { get; init; }

    public McpTransportKind Transport { get; init; } = McpTransportKind.Stdio;

    public McpTrust Trust { get; init; } = McpTrust.Untrusted;

    /// <summary>Stdio: the executable, found on the sandbox's PATH when it has no slash.</summary>
    public string? Command { get; init; }

    public ImmutableArray<string> Arguments { get; init; } = [];

    /// <summary>
    /// Stdio: plain settings for the server's environment. Secret-like names are refused (plan §10.5): a value that is a
    /// secret belongs in Vault, and the sandbox launcher refuses such names too.
    /// </summary>
    public ImmutableDictionary<string, string> Environment { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Stdio: run under the sandbox launcher (the default). False runs the server unconfined, with a cleared environment;
    /// only for a server the user chooses to trust that far.
    /// </summary>
    public bool Sandboxed { get; init; } = true;

    /// <summary>Stdio: the sandboxed server may use the network. Off by default, as for the shell.</summary>
    public bool Network { get; init; }

    /// <summary>Stdio: the only directory the server can write (absolute); the host gives each server a private one by default.</summary>
    public string? Workspace { get; init; }

    /// <summary>Stdio: further paths bound read-only (absolute), for example a project the server reads.</summary>
    public ImmutableArray<string> ReadOnlyPaths { get; init; } = [];

    /// <summary>HTTP: the endpoint. https is required when <see cref="Secret"/> is set: Vault sends credentials only over TLS.</summary>
    public Uri? Url { get; init; }

    /// <summary>HTTP: the Vault secret sent as <c>Authorization: Bearer</c>, as a placeholder Vault substitutes at its edge.</summary>
    public SecretRef? Secret { get; init; }

    /// <summary>The server's tools Corvid offers, by the server's names; empty offers all of them (up to <see cref="MaxTools"/>).</summary>
    public ImmutableArray<string> Tools { get; init; } = [];

    /// <summary>
    /// Tools the user declares read-only, by the server's names, whatever the server says: they run without asking. Results
    /// from an untrusted server still taint the session.
    /// </summary>
    public ImmutableArray<string> ReadOnlyTools { get; init; } = [];

    /// <summary>Connecting, the handshake and listing tools; a server that takes longer is marked failed and the host goes on.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>One tool call, or the request that starts a task.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A call the server runs as a task (the MCP tasks extension), polled until it finishes.</summary>
    public TimeSpan TaskTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Characters of a result kept for the model; the rest is cut and the result says so.</summary>
    public int MaxResultChars { get; init; } = 16_000;

    /// <summary>The most tools taken from the server; more are reported and left out.</summary>
    public int MaxTools { get; init; } = 64;

    /// <summary>Paths hidden from a sandboxed server in addition to the launcher's own denials: Vault's and Corvid's state.</summary>
    public ImmutableArray<string> DeniedPaths { get; init; } = [];

    /// <summary>True when what the server is sent can leave the machine: an HTTP server, or a stdio one with network or unconfined.</summary>
    public bool ReachesNetwork => Transport == McpTransportKind.Http || Network || !Sandboxed;

    public static bool IsValidName(string name) =>
        name is { Length: > 0 and <= 24 } && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>Every problem with the definition; empty when it may start.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var name = Name ?? "";
        if (!IsValidName(name))
            problems.Add($"'{name}' is not a valid server name (lower-case letters, digits, '-' or '_', starting with a letter; at most 24).");
        var prefix = $"MCP server '{name}': ";
        if (StartTimeout <= TimeSpan.Zero || CallTimeout <= TimeSpan.Zero || TaskTimeout <= TimeSpan.Zero)
            problems.Add(prefix + "timeouts must be positive.");
        if (MaxResultChars is < 256 or > 1_000_000)
            problems.Add(prefix + "MaxResultChars must be between 256 and 1000000.");
        if (MaxTools is < 1 or > 512)
            problems.Add(prefix + "MaxTools must be between 1 and 512.");
        switch (Transport)
        {
            case McpTransportKind.Stdio:
                if (string.IsNullOrWhiteSpace(Command))
                    problems.Add(prefix + "a stdio server needs a Command.");
                if (Url is not null || Secret is not null)
                    problems.Add(prefix + "Url and Secret are for HTTP servers; a stdio server gets no credential (Vault's exec path is not built).");
                foreach (var (key, _) in Environment)
                {
                    if (!SandboxPolicy.IsValidName(key))
                        problems.Add(prefix + $"'{key}' is not a valid environment variable name.");
                    else if (SandboxPolicy.IsSecretLikeName(key))
                        problems.Add(prefix + $"'{key}' looks like a secret. Secrets never go in configuration; they stay in Vault.");
                }
                if (Workspace is { } workspace && !Path.IsPathFullyQualified(workspace))
                    problems.Add(prefix + $"Workspace '{workspace}' is not an absolute path.");
                problems.AddRange(ReadOnlyPaths.Where(path => !Path.IsPathFullyQualified(path))
                    .Select(path => prefix + $"read-only path '{path}' is not absolute."));
                break;
            case McpTransportKind.Http:
                if (Url is null || !Url.IsAbsoluteUri || Url.Scheme is not ("https" or "http"))
                    problems.Add(prefix + "an HTTP server needs an absolute http or https Url.");
                else if (Secret is not null && Url.Scheme != "https")
                    problems.Add(prefix + "a server with a Vault secret must use https; Vault sends credentials only over verified TLS.");
                else if (!string.IsNullOrEmpty(Url.UserInfo))
                    problems.Add(prefix + "credentials do not go in the Url; put the token in Vault and name it as Secret.");
                if (Command is not null || !Arguments.IsEmpty || !Environment.IsEmpty)
                    problems.Add(prefix + "Command, Arguments and Environment are for stdio servers.");
                break;
            default:
                problems.Add(prefix + $"unknown transport '{Transport}'.");
                break;
        }
        return problems;
    }
}
