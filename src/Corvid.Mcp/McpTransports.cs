using System.Collections.Immutable;
using Corvid.Sandbox;
using Corvid.Vault.Client;
using ModelContextProtocol.Client;

namespace Corvid.Mcp;

/// <summary>What the default transports need from the host.</summary>
public sealed record McpTransportOptions
{
    /// <summary>The sandbox launcher's options (its path); the server's own environment is added per server.</summary>
    public SandboxOptions Sandbox { get; init; } = new();

    /// <summary>Vault, for servers with a secret. Without it such a server does not start: credentials come only from Vault.</summary>
    public VaultClient? Vault { get; init; }

    /// <summary>Placeholders minted in <see cref="Vault"/>'s session.</summary>
    public VaultPlaceholders? Placeholders { get; init; }

    /// <summary>The direct route for a keyless HTTP server (a test's fake); by default no proxy and no redirects.</summary>
    public HttpMessageHandler? Network { get; init; }
}

/// <summary>
/// The default transports (plan §10.1, §10.5). A stdio server runs under the sandbox launcher: its own workspace writable,
/// everything else read-only, Vault's and Corvid's state hidden, no network unless configured, and only plain environment
/// settings. An HTTP server with a credential is reached only through Vault's egress proxy with a placeholder in its
/// <c>Authorization</c> header; Corvid never holds the token, and a request without the placeholder cannot leave unproxied.
/// </summary>
public sealed class McpTransports(McpTransportOptions? options = null) : IMcpTransports
{
    // Headers that carry credentials; a keyless server receives none of them.
    private static readonly string[] CredentialHeaders = ["Authorization", "Proxy-Authorization", "x-api-key", "api-key"];

    private readonly McpTransportOptions _options = options ?? new McpTransportOptions();

    public IClientTransport Create(McpServerDefinition server, Action<string> standardError)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(standardError);
        return server.Transport switch
        {
            McpTransportKind.Stdio => Stdio(server, standardError),
            McpTransportKind.Http => Http(server),
            _ => throw new NotSupportedException($"Unknown MCP transport '{server.Transport}'."),
        };
    }

    /// <summary>The sandbox policy a stdio server runs under.</summary>
    public static SandboxPolicy Policy(McpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var workspace = server.Workspace ?? throw new InvalidOperationException($"MCP server '{server.Name}' has no workspace to run in.");
        return new SandboxPolicy
        {
            Workspace = workspace,
            ReadOnlyPaths = server.ReadOnlyPaths,
            DeniedPaths = server.DeniedPaths,
            Network = server.Network,
            Environment = [.. SandboxPolicy.DefaultEnvironment.Concat(server.Environment.Keys).Distinct(StringComparer.Ordinal)],
        };
    }

    private StdioClientTransport Stdio(McpServerDefinition server, Action<string> standardError)
    {
        var command = server.Command!;
        if (!server.Sandboxed)
        {
            // Unconfined only because the user said so for this server; still nothing inherited but plain settings.
            // The host gives every stdio server a private workspace that may not exist yet, and the process cannot start in a
            // missing working directory.
            if (server.Workspace is { } directory)
                Directory.CreateDirectory(directory);
            var plain = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var (name, value) in server.Environment)
                plain[name] = value;
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = command,
                Arguments = [.. server.Arguments],
                WorkingDirectory = server.Workspace,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = plain,
                StandardErrorLines = standardError,
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            });
        }

        if (!OperatingSystem.IsLinux())
            throw new SandboxRefusedException($"MCP server '{server.Name}' runs sandboxed, and the sandbox runs on Linux only; it was not started.");
        var workspace = server.Workspace ?? throw new InvalidOperationException($"MCP server '{server.Name}' has no workspace to run in.");
        // The launcher binds only paths that exist.
        Directory.CreateDirectory(workspace);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                environment[key] = value;
        }
        foreach (var (name, value) in _options.Sandbox.Environment ?? ImmutableDictionary<string, string>.Empty)
            environment[name] = value;
        foreach (var (name, value) in server.Environment)
            environment[name] = value;
        var sandbox = new BubblewrapSandbox(_options.Sandbox with { Environment = environment });
        var launch = sandbox.Prepare(Policy(server), new SandboxCommand(command, server.Arguments) { WorkingDirectory = workspace });
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = launch.Launcher,
            Arguments = [.. launch.Arguments],
            InheritEnvironmentVariables = false,
            EnvironmentVariables = launch.Environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal),
            StandardErrorLines = standardError,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
    }

    private HttpClientTransport Http(McpServerDefinition server)
    {
        HttpMessageHandler handler;
        if (server.Secret is { } secret)
        {
            if (_options.Vault is null || _options.Placeholders is null)
                throw new McpServerUnavailableException($"MCP server '{server.Name}' needs the Vault secret '{secret.Name}', and Vault is not configured.");
            handler = new RequestContextHandler(server.Name, new VaultSecretHandler(_options.Placeholders, new SecretHeader(secret, "Authorization", "Bearer"),
                new VaultHandler(_options.Vault, new NoDirectRoute(server.Name))));
        }
        else
        {
            handler = new RequestContextHandler(server.Name, new NoCredentials(_options.Network ?? Direct()));
        }

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = server.Url!,
            // Streamable HTTP only: no fallback to the old SSE transport, and no standalone GET stream, which Vault (it
            // buffers whole responses) could never finish relaying. Answers to Corvid's own requests still arrive.
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            ConnectionTimeout = server.StartTimeout,
            MaxReconnectionAttempts = 0,
        }, client, loggerFactory: null, ownsHttpClient: true);
    }

    private static SocketsHttpHandler Direct() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // Tells Vault which server the request is for and whether the calling session holds outside content.
    private sealed class RequestContextHandler(string server, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Requests outside a tool call (connecting, listing) carry no session content; a call says what its session holds.
            request.Options.Set(VaultHandler.Tainted, McpRequestScope.Value?.Tainted ?? false);
            request.Options.Set(VaultHandler.Toolset, "mcp:" + server);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class NoCredentials(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var name in CredentialHeaders)
                request.Headers.Remove(name);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class NoDirectRoute(string server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"A request to MCP server '{server}' reached the network without Vault; it was not sent.");
    }
}
