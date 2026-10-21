using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Corvid.Models.Remote.Tests;
using Corvid.Vault.Contracts;

namespace Corvid.Gateway.Tests;

/// <summary>What the gateway gives an adapter, in memory.</summary>
internal sealed class MemoryContext(params string[] users) : IAdapterContext
{
    public ConcurrentDictionary<string, string> Cursors { get; } = new();

    public List<string> Users { get; } = [.. users];

    public IReadOnlyList<string> KnownUsers() => [.. Users];

    public string? GetCursor(string key) => Cursors.TryGetValue(key, out var value) ? value : null;

    public void SetCursor(string key, string value) => Cursors[key] = value;
}

/// <summary>A platform behind the stand-in Vault: each forwarded request is answered by <see cref="Answer"/>.</summary>
internal sealed class PlatformBehindVault : IAsyncDisposable
{
    public PlatformBehindVault(Func<ForwardRequest, ForwardResponse> answer)
    {
        Vault = new FakeVault(answer);
        Gateway = GatewayVault.Create(Vault.SocketPath);
    }

    public FakeVault Vault { get; }

    public GatewayVault Gateway { get; }

    public IReadOnlyList<ForwardRequest> Requests => [.. Vault.Forwarded];

    public static ForwardResponse Json(string json, int status = 200, params WireHeader[] headers) => new()
    {
        Status = status,
        Headers = [new WireHeader("Content-Type", "application/json"), .. headers],
        Body = Encoding.UTF8.GetBytes(json),
    };

    public static JsonNode? Body(ForwardRequest request) => request.Body is { Length: > 0 } body ? JsonNode.Parse(body) : null;

    public ValueTask DisposeAsync() => Vault.DisposeAsync();
}

/// <summary>A WebSocket the test drives: frames written by the test arrive at the adapter; what it sends is recorded.</summary>
internal sealed class FakeSockets : IWebSocketConnector
{
    public ConcurrentQueue<Uri> Opened { get; } = new();

    public FakeConnection? Current { get; private set; }

    public Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        Opened.Enqueue(uri);
        Current = new FakeConnection();
        return Task.FromResult<IWebSocketConnection>(Current);
    }

    internal sealed class FakeConnection : IWebSocketConnection
    {
        private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();

        public ConcurrentQueue<string> Sent { get; } = new();

        public bool Disposed;

        public void Push(string? frame) => _inbound.Writer.TryWrite(frame);

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) => await _inbound.Reader.ReadAsync(cancellationToken);

        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            Sent.Enqueue(text);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
