using System.Net.Sockets;
using Corvid.Vault.Contracts;

namespace Corvid.Vault.Client;

/// <summary>
/// Talks to Corvid Vault over its Unix socket. One connection per call; the socket is local, so connecting is cheap and
/// no connection state can leak between calls.
/// </summary>
public sealed class VaultClient
{
    public VaultClient(string sessionId, string? socketPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        SocketPath = socketPath ?? DefaultSocketPath();
    }

    public string SessionId { get; }

    public string SocketPath { get; }

    /// <summary><c>$XDG_RUNTIME_DIR/corvid-vault/vault.sock</c>.</summary>
    public static string DefaultSocketPath()
    {
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime))
        {
            throw new InvalidOperationException("XDG_RUNTIME_DIR is not set; pass the Vault socket path explicitly.");
        }

        return Path.Combine(runtime, "corvid-vault", "vault.sock");
    }

    public async Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<CatalogResponse>(new CatalogRequest { SessionId = SessionId }, cancellationToken).ConfigureAwait(false)).Secrets;

    /// <summary>Mints a placeholder for <paramref name="secret"/>, valid for this session only.</summary>
    public async Task<string> MintPlaceholderAsync(SecretRef secret, CancellationToken cancellationToken = default) =>
        (await SendAsync<PlaceholderResponse>(new PlaceholderRequest { SessionId = SessionId, Secret = secret.Name }, cancellationToken)
            .ConfigureAwait(false)).Placeholder;

    /// <summary>Mints a placeholder for <paramref name="secret"/>, with the time it expires.</summary>
    public Task<PlaceholderResponse> RequestPlaceholderAsync(SecretRef secret, CancellationToken cancellationToken = default) =>
        SendAsync<PlaceholderResponse>(new PlaceholderRequest { SessionId = SessionId, Secret = secret.Name }, cancellationToken);

    /// <summary>Replaces any secret value in <paramref name="text"/> with <c>[secret:name]</c>.</summary>
    public async Task<string> ScrubAsync(string text, CancellationToken cancellationToken = default) =>
        (await SendAsync<ScrubResponse>(new ScrubRequest { SessionId = SessionId, Text = text }, cancellationToken).ConfigureAwait(false)).Text;

    public async Task<IReadOnlyDictionary<string, int>> GetAuditSummaryAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<AuditSummaryResponse>(new AuditSummaryRequest { SessionId = SessionId }, cancellationToken).ConfigureAwait(false)).Counts;

    public Task<ForwardResponse> ForwardAsync(ForwardRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ForwardResponse>(request, cancellationToken);

    /// <summary>
    /// Opens a sandbox proxy for this session (V-003a): a socket the sandbox's HTTPS proxy client connects to, and the CA
    /// certificate it must trust. Close it with <see cref="CloseSandboxAsync"/> when the command ends.
    /// </summary>
    public Task<SandboxOpenResponse> OpenSandboxAsync(
        string? toolset, bool tainted, bool network, int? lifetimeMinutes = null, CancellationToken cancellationToken = default) =>
        SendAsync<SandboxOpenResponse>(
            new SandboxOpenRequest { SessionId = SessionId, Toolset = toolset, Tainted = tainted, Network = network, LifetimeMinutes = lifetimeMinutes },
            cancellationToken);

    /// <summary>Closes a sandbox proxy this session opened: its socket, live connections and CA go away.</summary>
    public Task<SandboxClosedResponse> CloseSandboxAsync(string sandboxId, CancellationToken cancellationToken = default) =>
        SendAsync<SandboxClosedResponse>(new SandboxCloseRequest { SessionId = SessionId, SandboxId = sandboxId }, cancellationToken);

    /// <summary>Sends one request. An <see cref="ErrorResponse"/> becomes a <see cref="VaultException"/>.</summary>
    public async Task<TResponse> SendAsync<TResponse>(VaultRequest request, CancellationToken cancellationToken = default)
        where TResponse : VaultResponse
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await VaultWire.WriteRequestAsync(stream, request, cancellationToken).ConfigureAwait(false);
        VaultResponse response = await VaultWire.ReadResponseAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new VaultException(VaultErrorCodes.BadRequest, "Vault closed the connection without a response.");
        return response switch
        {
            TResponse typed => typed,
            ErrorResponse error => throw new VaultException(error.Code, error.Message),
            _ => throw new VaultException(VaultErrorCodes.BadRequest, $"Unexpected response {response.GetType().Name}."),
        };
    }
}

/// <summary>Vault refused or failed a request. <see cref="Code"/> is one of <see cref="VaultErrorCodes"/>.</summary>
public sealed class VaultException : Exception
{
    public VaultException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public VaultException()
        : this(VaultErrorCodes.BadRequest, "Vault error.")
    {
    }

    public VaultException(string message)
        : this(VaultErrorCodes.BadRequest, message)
    {
    }

    public VaultException(string message, Exception innerException)
        : base(message, innerException) => Code = VaultErrorCodes.BadRequest;

    public string Code { get; }
}
