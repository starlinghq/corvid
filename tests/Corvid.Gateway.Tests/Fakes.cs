using System.Collections.Concurrent;
using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using System.Threading.Channels;
using Corvid.Approvals;
using Corvid.Kernel.Events;

namespace Corvid.Gateway.Tests;

/// <summary>A platform with no network: the test writes inbound messages and reads what was sent.</summary>
internal sealed class FakeAdapter(string name = "fake") : IGatewayAdapter
{
    private readonly Channel<InboundMessage> _inbound = Channel.CreateUnbounded<InboundMessage>();

    public string Name => name;

    public ConcurrentQueue<OutboundMessage> Sent { get; } = new();

    /// <summary>Thrown by the next sends, one per send, instead of sending.</summary>
    public ConcurrentQueue<Exception> SendFailures { get; } = new();

    /// <summary>Makes every send fail until cleared.</summary>
    public Exception? AlwaysFail { get; set; }

    public int Connects;

    public bool Disposed;

    public IAdapterContext? Context { get; private set; }

    public void Write(InboundMessage message) => _inbound.Writer.TryWrite(message with { Adapter = name });

    public Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        Context = context;
        Interlocked.Increment(ref Connects);
        return Task.CompletedTask;
    }

    public async Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken)
    {
        await foreach (var message in _inbound.Reader.ReadAllAsync(cancellationToken))
            await inbox(message, cancellationToken);
    }

    public Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        if (AlwaysFail is { } always)
            throw always;
        if (SendFailures.TryDequeue(out var failure))
            throw failure;
        Sent.Enqueue(message);
        return Task.FromResult(new SentMessage("m" + Sent.Count));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>An adapter whose connect never finishes and ignores cancellation, like a hanging Telegram connect.</summary>
internal sealed class HangingAdapter(string name, ManualResetEventSlim release) : IGatewayAdapter
{
    public string Name => name;

    public int Attempts;

    public Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Attempts);
        // Blocks a thread and never looks at the token.
        return Task.Run(() => release.Wait(), CancellationToken.None);
    }

    public Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);

    public Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken) => throw new InvalidOperationException("never connected");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>The agent side: records what the gateway asked and answers with <see cref="Answer"/>.</summary>
internal sealed class FakeAgent : IGatewayAgent
{
    public ConcurrentQueue<(string Session, string Adapter, ChatSettings Settings)> Opened { get; } = new();

    public ConcurrentQueue<(string Session, string Text, bool FromOwner)> Posts { get; } = new();

    public ConcurrentQueue<(string Session, string Line)> Commands { get; } = new();

    public ConcurrentDictionary<string, IApprovalPrompter> Prompters { get; } = new();

    public Func<string, string, bool, AgentReply> Answer { get; set; } = (_, text, fromOwner) => new AgentReply($"echo: {text}", "reply", !fromOwner, 3);

    /// <summary>Returned (already faulted) by the next opens, one per open, as an agent that throws before its first await does.</summary>
    public ConcurrentQueue<Exception> OpenFailures { get; } = new();

    public Task OpenAsync(string sessionId, string adapter, ChatSettings settings, CancellationToken cancellationToken)
    {
        if (OpenFailures.TryDequeue(out var failure))
            return Task.FromException(failure);
        Opened.Enqueue((sessionId, adapter, settings));
        return Task.CompletedTask;
    }

    public ValueTask<Task<AgentReply>> PostAsync(string sessionId, string text, bool fromOwner, CancellationToken cancellationToken)
    {
        Posts.Enqueue((sessionId, text, fromOwner));
        return ValueTask.FromResult(Task.FromResult(Answer(sessionId, text, fromOwner)));
    }

    public bool IsCommand(string text) => text.StartsWith('/') && text.Length > 1 && char.IsAsciiLetter(text[1]);

    public Task<string> RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken)
    {
        Commands.Enqueue((sessionId, line));
        return Task.FromResult($"ran {line}");
    }

    public IDisposable AttachPrompter(string sessionId, IApprovalPrompter prompter)
    {
        Prompters[sessionId] = prompter;
        return new Detach(() => Prompters.TryRemove(sessionId, out _));
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>A temporary data directory with an event log and a pairing store, and gateways built over them.</summary>
internal sealed class GatewayFixture : IAsyncDisposable
{
    private readonly List<GatewayHost> _hosts = [];

    public GatewayFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Log = new SqliteEventLog(Path.Combine(Root, "events.db"));
        Pairing = new PairingStore(Path.Combine(Root, "gateway.db"));
    }

    public string Root { get; }

    public SqliteEventLog Log { get; }

    public PairingStore Pairing { get; }

    public FakeAgent Agent { get; } = new();

    /// <summary>What the gateways said through <see cref="GatewayHost.Notice"/>.</summary>
    public ConcurrentQueue<string> Notices { get; } = new();

    public IReadOnlyList<StoredEvent> Ledger() => Log.Read(GatewaySessions.Ledger, limit: 4096, cancellationToken: TestContext.Current.CancellationToken);

    public static GatewayOptions Options(params AdapterOptions[] adapters) => new()
    {
        Adapters = adapters.ToImmutableDictionary(adapter => adapter.Name),
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        StopTimeout = TimeSpan.FromSeconds(2),
        ReconnectDelay = TimeSpan.FromMilliseconds(50),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(200),
        LedgerPoll = TimeSpan.FromMilliseconds(50),
        RetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        MaxDeliveryAttempts = 4,
    };

    public async Task<GatewayHost> StartAsync(GatewayOptions options, params IGatewayAdapter[] adapters)
    {
        var host = new GatewayHost(adapters.Select(adapter => new GatewayAdapterRegistration(adapter.Name, () => adapter)), Agent, Pairing,
            new DeliveryLedger(Log), options) { Notice = Notices.Enqueue };
        await host.StartAsync(TestContext.Current.CancellationToken);
        _hosts.Add(host);
        return host;
    }

    public async Task<GatewayHost> StartAsync(GatewayOptions options, IEnumerable<GatewayAdapterRegistration> registrations)
    {
        var host = new GatewayHost(registrations, Agent, Pairing, new DeliveryLedger(Log), options) { Notice = Notices.Enqueue };
        await host.StartAsync(TestContext.Current.CancellationToken);
        _hosts.Add(host);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails after five seconds.</summary>
    public static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting: " + because);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
