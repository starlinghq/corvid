using System.Net.Sockets;
using System.Net.WebSockets;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;
using Corvid.Tests;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Attached mode's message limits with images (A6, K-8 over G-1): the server reads small requests until a connection has
/// authenticated as the owner or a paired device's token, and messages with images only then; pairing alone raises
/// nothing. An engine that takes no images refuses them on the client, before anything crosses the wire.
/// </summary>
public sealed class AttachedLimitTests
{
    private const string OwnerToken = "owner-token-0123456789abcdefghijklmnop";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The server's end, recording every limit the server raises.</summary>
    private sealed class LimitChannel(IMessageChannel inner) : IMessageChannel
    {
        public List<int> Allowed { get; } = [];

        public ValueTask SendAsync(string message, CancellationToken cancellationToken) => inner.SendAsync(message, cancellationToken);

        public ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken) => inner.ReceiveAsync(cancellationToken);

        public ValueTask CloseAsync(string reason, CancellationToken cancellationToken) => inner.CloseAsync(reason, cancellationToken);

        public string? ClosedBecause => inner.ClosedBecause;

        public void AllowMessagesUpTo(int maxMessageBytes)
        {
            lock (Allowed)
                Allowed.Add(maxMessageBytes);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static (AttachedServer Server, DemoSessionService Engine) Serve()
    {
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        return (new AttachedServer(engine, new AttachedSessionTests.FakeAuthenticator()), engine);
    }

    private static (InMemoryMessageChannel Client, LimitChannel Server, Task Served) Open(AttachedServer server, CancellationToken stop)
    {
        var (client, end) = InMemoryMessageChannel.CreatePair();
        var limited = new LimitChannel(end);
        return (client, limited, server.ServeAsync(limited, "test", stop));
    }

    [Fact]
    public async Task OnlyAnAuthenticatedConnectionMaySendMessagesWithImages()
    {
        var (server, _) = Serve();
        using var stop = new CancellationTokenSource();

        var refused = Open(server, stop.Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            RemoteSessionService.ConnectAsync(refused.Client, "not-the-owner-token-0123456789abcdef", "test", new InlineDispatcher(), Ct));
        await refused.Served.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Empty(refused.Server.Allowed);

        var pairing = Open(server, stop.Token);
        var token = await RemoteSessionService.PairAsync(pairing.Client, "PAIR-1234", "phone", Ct);
        Assert.NotEmpty(token);
        Assert.Empty(pairing.Server.Allowed);

        var owner = Open(server, stop.Token);
        await using (var client = await RemoteSessionService.ConnectAsync(owner.Client, OwnerToken, "test", new InlineDispatcher(), Ct))
            Assert.Equal([AttachedProtocol.MaxAuthenticatedRequestBytes], owner.Server.Allowed);
        var device = Open(server, stop.Token);
        await using (var client = await RemoteSessionService.ConnectAsync(device.Client, token, "phone", new InlineDispatcher(), Ct))
            Assert.Equal([AttachedProtocol.MaxAuthenticatedRequestBytes], device.Server.Allowed);
        await stop.CancelAsync();
    }

    [Fact]
    public async Task AnEngineThatTakesNoImagesRefusesThemOnTheClient()
    {
        var (server, _) = Serve();
        using var stop = new CancellationTokenSource();
        var owner = Open(server, stop.Token);
        await using var client = await RemoteSessionService.ConnectAsync(owner.Client, OwnerToken, "test", new InlineDispatcher(), Ct);

        Assert.False(client.CanSendImages);
        Assert.False(client.CanChooseWorkspace);
        await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            client.SendAsync(DemoSessionService.MainSessionId, "look", [new ImageAttachment(TestImages.Png())], Ct));
        await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.CreateAsync(new NewSessionRequest("/elsewhere"), Ct));
        // Without images a send is the plain one, as before.
        await client.SendAsync(DemoSessionService.MainSessionId, "hello", [], Ct);
        await stop.CancelAsync();
    }

    [Fact]
    public async Task AWebSocketReadsALargerMessageOnlyOnceItsLimitIsRaised()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(Path.GetTempPath(), "corvid-limit-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(2);
        try
        {
            async Task<(WebSocketMessageChannel Reader, WebSocketMessageChannel Writer)> PairAsync()
            {
                var connecting = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await connecting.ConnectAsync(new UnixDomainSocketEndPoint(path), Ct);
                var accepted = await listener.AcceptAsync(Ct);
                var reader = WebSocket.CreateFromStream(new NetworkStream(accepted, ownsSocket: true), isServer: true, null, TimeSpan.Zero);
                var writer = WebSocket.CreateFromStream(new NetworkStream(connecting, ownsSocket: true), isServer: false, null, TimeSpan.Zero);
                return (new WebSocketMessageChannel(reader, AttachedProtocol.MaxRequestBytes),
                    new WebSocketMessageChannel(writer, AttachedProtocol.MaxResponseBytes));
            }
            var large = new string('a', AttachedProtocol.MaxRequestBytes + 1);

            var (before, sender) = await PairAsync();
            await using (before)
            await using (sender)
            {
                var sending = sender.SendAsync(large, Ct).AsTask();
                await Assert.ThrowsAsync<InvalidDataException>(() => before.ReceiveAsync(Ct).AsTask());
                _ = sending;
            }

            var (after, sender2) = await PairAsync();
            await using (after)
            await using (sender2)
            {
                after.AllowMessagesUpTo(AttachedProtocol.MaxAuthenticatedRequestBytes);
                // Never lowered.
                after.AllowMessagesUpTo(1024);
                var sending = sender2.SendAsync(large, Ct).AsTask();
                Assert.Equal(large, await after.ReceiveAsync(Ct));
                await sending;
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
