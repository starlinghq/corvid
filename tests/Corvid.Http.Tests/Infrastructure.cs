using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Tests;
using Corvid.Vault.Contracts;
using Microsoft.AspNetCore.Builder;

namespace Corvid.Http.Tests;

/// <summary>
/// <c>corvid serve</c> on fake models: the real endpoint (Kestrel on a loopback port the system chooses) and an
/// <see cref="HttpClient"/> that talks to it.
/// </summary>
internal sealed class Served : IAsyncDisposable
{
    private Served(TestHost host, CorvidRuntime runtime, WebApplication app, HttpClient client)
    {
        Host = host;
        Runtime = runtime;
        App = app;
        Client = client;
    }

    public TestHost Host { get; }

    public CorvidRuntime Runtime { get; }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public string Workspace => Path.Combine(Host.Root, "work");

    public static async Task<Served> StartAsync(TestHost host, Action<ServeSettings>? configure = null, IBearerTokenVerifier? verifier = null)
    {
        var runtime = await host.StartAsync();
        var settings = new ServeSettings { Url = "http://127.0.0.1:0" };
        configure?.Invoke(settings);
        var app = OpenAIEndpoint.Build(runtime, new OpenAIEndpointOptions(settings, verifier));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = new HttpClient { BaseAddress = OpenAIEndpoint.ListeningOn(app), Timeout = TimeSpan.FromSeconds(60) };
        return new Served(host, runtime, app, client);
    }

    public static StringContent Json(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static object Chat(params (string Role, string Content)[] messages) =>
        new { model = "corvid", messages = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray() };

    public static object ChatStream(params (string Role, string Content)[] messages) =>
        new { model = "corvid", stream = true, messages = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray() };

    public Task<HttpResponseMessage> PostAsync(object body, string? session = null, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json(body) };
        if (session is not null)
            request.Headers.Add(OpenAIEndpoint.SessionHeader, session);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonObject> BodyAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();

    /// <summary>A server-sent event stream as its <c>data:</c> payloads, in order.</summary>
    public static async Task<List<string>> EventsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return [.. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(block =>
        {
            Assert.StartsWith("data: ", block, StringComparison.Ordinal);
            return block["data: ".Length..];
        })];
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
        await Runtime.DisposeAsync();
        await Host.DisposeAsync();
    }
}

/// <summary>A bearer-token check with a fixed answer, counting the tokens it was shown.</summary>
internal sealed class FixedVerifier(bool? answer) : IBearerTokenVerifier
{
    public List<string> Seen { get; } = [];

    public ValueTask<bool?> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        lock (Seen)
            Seen.Add(token);
        return ValueTask.FromResult(answer);
    }
}

/// <summary>
/// A stand-in for corvid-vault's catalogue and scrub service on a Unix socket, holding one secret. It scrubs the value and
/// its hex form, as the real scrubber does among other encodings, and counts scrub requests.
/// </summary>
internal sealed class ScrubbingVault : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _root;
    private readonly string _name;
    private readonly string _value;
    private int _scrubs;

    public ScrubbingVault(string name, string value)
    {
        _name = name;
        _value = value;
        // Short, because a Unix socket path is limited to 108 bytes.
        _root = Path.Combine(Path.GetTempPath(), "cvh-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_root);
        SocketPath = Path.Combine(_root, "v.sock");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(16);
        _loop = Task.Run(AcceptAsync);
    }

    public string SocketPath { get; }

    public int Scrubs => Volatile.Read(ref _scrubs);

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
            CatalogRequest => new CatalogResponse { Secrets = [new CatalogEntry(_name, "the serve token", [], [])] },
            ScrubRequest scrub => Scrub(scrub.Text),
            _ => new ErrorResponse { Code = VaultErrorCodes.BadRequest, Message = "unexpected request" },
        };
        await VaultWire.WriteResponseAsync(stream, response, _stop.Token);
    }

    private ScrubResponse Scrub(string text)
    {
        Interlocked.Increment(ref _scrubs);
        var marker = $"[secret:{_name}]";
        var hits = 0;
        foreach (var variant in new[] { _value, Convert.ToHexStringLower(Encoding.UTF8.GetBytes(_value)) })
        {
            var parts = text.Split(variant);
            hits += parts.Length - 1;
            text = string.Join(marker, parts);
        }
        return new ScrubResponse { Text = text, Hits = hits };
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
