using System.Net.WebSockets;
using System.Text;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Gateway;

/// <summary>Vault, as the gateway's adapters use it: one Vault session for the gateway process, and its placeholders.</summary>
public sealed record GatewayVault(VaultClient Client, VaultPlaceholders Placeholders)
{
    public static GatewayVault Create(string socketPath, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var client = new VaultClient("corvid-gateway-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8], socketPath);
        return new GatewayVault(client, new VaultPlaceholders(client, time));
    }
}

/// <summary>
/// The HTTP pipeline under every adapter (plan §10.5, vault.md §9): bot tokens never enter Corvid. Each request carries a
/// Vault placeholder where the token goes (a header for Slack and Discord, the URL path for Telegram) and is sent by Vault's
/// egress proxy, which substitutes the value for hosts the secret is bound to and scrubs the response. Below the proxy sits a
/// handler that refuses, so a request without a placeholder never leaves unproxied. Requests name the toolset
/// <see cref="Toolset"/>, so a bot token can be limited to the gateway in <c>vault.json</c>.
/// </summary>
public static class GatewayHttp
{
    public const string Toolset = "gateway";

    /// <summary>For a token carried in a header (<c>Authorization: Bot …</c>, <c>Authorization: Bearer …</c>).</summary>
    public static HttpClient Create(GatewayVault vault, SecretHeader credential, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(credential);
        return Client(new VaultSecretHandler(vault.Placeholders, credential, new VaultHandler(vault.Client, new NoDirectRoute())), timeout);
    }

    /// <summary>For a token carried in the URL path: callers write <see cref="VaultUrlSecretHandler.Marker"/> where it goes.</summary>
    public static HttpClient CreateForUrl(GatewayVault vault, SecretRef secret, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(vault);
        return Client(new VaultUrlSecretHandler(vault.Placeholders, secret, new VaultHandler(vault.Client, new NoDirectRoute())), timeout);
    }

    /// <summary>Marks a request as carrying text from a tainted turn (plan §10.3), which Vault's policy may treat more strictly.</summary>
    public static HttpRequestMessage Tainted(this HttpRequestMessage request, bool tainted)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(VaultHandler.Tainted, tainted);
        return request;
    }

    /// <summary>
    /// A send Vault refused, as the gateway's delivery loop understands it. Policy refusals (a tainted answer under the
    /// secret's default <c>tainted: ask</c> rule, a host or toolset the secret is not bound to, a removed secret) will not
    /// change on a retry, so they are given up at once with Vault's reason; budgets, rate limits and upstream failures are
    /// retried.
    /// </summary>
    public static GatewaySendException SendRefused(string what, VaultException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var transient = exception.Code is VaultErrorCodes.RateLimited or VaultErrorCodes.BudgetExceeded or VaultErrorCodes.UpstreamError
            or VaultErrorCodes.BadPlaceholder;
        return new GatewaySendException($"{what}: Vault refused it ({exception.Message})", permanent: !transient, inner: exception);
    }

    private static HttpClient Client(HttpMessageHandler credential, TimeSpan timeout) =>
        new(new ToolsetHandler(credential)) { Timeout = timeout };

    private sealed class ToolsetHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.Set(VaultHandler.Toolset, Toolset);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class NoDirectRoute : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"A gateway request to {request.RequestUri?.Host} reached the network without Vault; it was not sent.");
    }
}

/// <summary>A text WebSocket, so adapters that receive over one (Slack's Socket Mode) can be tested without a network.</summary>
public interface IWebSocketConnection : IAsyncDisposable
{
    /// <summary>The next text message, or null when the other side closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    Task SendAsync(string text, CancellationToken cancellationToken);
}

public interface IWebSocketConnector
{
    Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ClientWebSocket"/>, connected directly: Vault does not relay WebSockets (vault.md §9's first-frame substitution is
/// not built), so only a URL that carries no credential may be opened here, such as Slack's one-time Socket Mode URL.
/// </summary>
public sealed class ClientWebSocketConnector : IWebSocketConnector
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    public async Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != "wss")
            throw new InvalidOperationException($"Only wss:// WebSockets are opened, not {uri.Scheme}://.");
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return new Connection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Connection(ClientWebSocket socket) : IWebSocketConnection
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                    throw new InvalidDataException("A WebSocket message was larger than 4 MB.");
                if (result.EndOfMessage)
                    return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) : "";
            }
        }

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
            socket.Dispose();
            _send.Dispose();
        }
    }
}
