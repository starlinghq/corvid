using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Models.Remote;
using Corvid.Vault.Contracts;

namespace Corvid.Models.Remote.Tests;

/// <summary>
/// A stand-in for corvid-vault on a Unix socket: it mints placeholders and answers forward requests with a scripted
/// provider response, recording what it was asked to send. No network and no secret values are involved.
/// </summary>
internal sealed class FakeVault : IAsyncDisposable
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _root;

    public FakeVault(Func<ForwardRequest, ForwardResponse> forward)
    {
        Forward = forward;
        // Short, because a Unix socket path is limited to 108 bytes.
        _root = Corvid.Tests.ShortTemp.Create("cv-");
        SocketPath = Path.Combine(_root, "vault.sock");
        _listener = Corvid.Tests.ShortTemp.Listen(_root, SocketPath);
        _loop = Task.Run(AcceptAsync);
    }

    public string SocketPath { get; }

    public Func<ForwardRequest, ForwardResponse> Forward { get; set; }

    public ConcurrentQueue<ForwardRequest> Forwarded { get; } = new();

    public ConcurrentQueue<string> Minted { get; } = new();

    /// <summary>What a catalogue request returns: names and bound hosts, never values.</summary>
    public List<CatalogEntry> Catalogue { get; } = [];

    /// <summary>What an audit summary request returns (<c>op.decision</c> counts); null answers it as an unexpected request.</summary>
    public Dictionary<string, int>? AuditCounts { get; set; }

    /// <summary>Errors returned to the next forward requests instead of forwarding them.</summary>
    public ConcurrentQueue<(string Code, string Message)> Refusals { get; } = new();

    public VaultAccess Access() => VaultAccess.Create(SocketPath);

    public static JsonNode Body(ForwardRequest request) => JsonNode.Parse(request.Body!)!;

    public static string? Header(ForwardRequest request, string name) =>
        request.Headers.LastOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static ForwardResponse Json(string json, int status = 200) => new()
    {
        Status = status,
        Headers = [new WireHeader("Content-Type", "application/json")],
        Body = Encoding.UTF8.GetBytes(json),
    };

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(connection));
        }
    }

    private async Task ServeAsync(Socket connection)
    {
        await using var stream = new NetworkStream(connection, ownsSocket: true);
        var request = await VaultWire.ReadRequestAsync(stream, _stop.Token);
        VaultResponse response = request switch
        {
            PlaceholderRequest mint => Mint(mint),
            CatalogRequest => new CatalogResponse { Secrets = [.. Catalogue] },
            AuditSummaryRequest when AuditCounts is { } counts => new AuditSummaryResponse { Counts = new Dictionary<string, int>(counts) },
            ForwardRequest forward => Relay(forward),
            // V-003b's doctor check opens and closes a sandbox proxy; nothing listens on the returned socket.
            SandboxOpenRequest open => OpenSandbox(open),
            SandboxCloseRequest close => CloseSandbox(close),
            _ => new ErrorResponse { Code = VaultErrorCodes.BadRequest, Message = "unexpected request" },
        };
        await VaultWire.WriteResponseAsync(stream, response, _stop.Token);
    }

    /// <summary>Sandbox proxies opened and closed (V-003b), by id.</summary>
    public ConcurrentQueue<string> SandboxesOpened { get; } = new();

    public ConcurrentQueue<string> SandboxesClosed { get; } = new();

    private SandboxOpenResponse OpenSandbox(SandboxOpenRequest request)
    {
        var id = "fakesandbox" + SandboxesOpened.Count.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        SandboxesOpened.Enqueue(id);
        return new SandboxOpenResponse
        {
            SandboxId = id,
            ProxySocketPath = Path.Combine(_root, "sandbox", id + ".sock"),
            CaCertificatePem = "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(request.LifetimeMinutes ?? 1),
        };
    }

    private SandboxClosedResponse CloseSandbox(SandboxCloseRequest request)
    {
        SandboxesClosed.Enqueue(request.SandboxId);
        return new SandboxClosedResponse { SandboxId = request.SandboxId };
    }

    private PlaceholderResponse Mint(PlaceholderRequest request)
    {
        var placeholder = Placeholders.Format(request.Secret, RandomNumberGenerator.GetString(Alphabet, Placeholders.TokenLength));
        Minted.Enqueue(placeholder);
        return new PlaceholderResponse { Placeholder = placeholder, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
    }

    private VaultResponse Relay(ForwardRequest request)
    {
        Forwarded.Enqueue(request);
        if (Refusals.TryDequeue(out var refusal))
            return new ErrorResponse { Code = refusal.Code, Message = refusal.Message };
        // Only a placeholder this Vault minted is substituted; anything else is refused, as the real one does.
        // A URL placeholder (Telegram's bot path, C-020) counts too, as it does for a secret with allowInUrl.
        var minted = Minted.ToArray();
        if (!request.Headers.Any(header => minted.Any(placeholder => header.Value.Contains(placeholder, StringComparison.Ordinal)))
            && !minted.Any(placeholder => request.Url.Contains(placeholder, StringComparison.Ordinal)))
            return new ErrorResponse { Code = VaultErrorCodes.BadPlaceholder, Message = "unknown placeholder" };
        return Forward(request);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}

/// <summary>The direct network route: records requests and answers with a script. For cloud models it must stay empty.</summary>
internal sealed class FakeNetwork(Func<HttpRequestMessage, string, HttpResponseMessage>? answer = null) : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));
        return answer?.Invoke(request, body) ?? new HttpResponseMessage(HttpStatusCode.InternalServerError);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class Profiles
{
    public static ModelProfile Cloud(string id, PromptCachingStyle caching = PromptCachingStyle.Breakpoints) =>
        new(id, 1_000_000, 200_000, ToolCallSupport.Native, StructuredOutputSupport.JsonSchema, caching, new ModelCost(4m, 20m, 0.4m),
            PrivacyClass.Cloud, "remote");

    public static ModelProfile LocalServer(string id) =>
        new(id, 32_768, 32_768, ToolCallSupport.Native, StructuredOutputSupport.JsonSchema, PromptCachingStyle.Automatic, ModelCost.Free,
            PrivacyClass.LocalServer, "http://127.0.0.1:8080");

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
