using System.Net;
using System.Net.WebSockets;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;
using Corvid.Cli;

namespace Corvid.Cli.Tests;

/// <summary>
/// <c>corvid serve --attach</c>'s listener (G-1) on real sockets: a Unix socket only its owner can open, a token needed on
/// it as on TCP, loopback host names only on a loopback address, and a browser only from the pages the server itself serves.
/// </summary>
public sealed class AttachEndpointTests : IAsyncDisposable
{
    private const string Owner = "owner-token-0123456789abcdefghijklmnop";

    private readonly string _directory = Directory.CreateTempSubdirectory("corvid-attach-ep-").FullName;
    private readonly CancellationTokenSource _stop = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Microsoft.AspNetCore.Builder.WebApplication> StartAsync(string? socket, Uri? listen, string? web = null)
    {
        var engine = new DemoSessionService(new InlineDispatcher(), Corvid.App.Core.FrozenTimeProvider.Demo());
        var authenticator = new AttachedAuthenticator((token, _) => ValueTask.FromResult<bool?>(token == Owner), Path.Combine(_directory, "devices.json"));
        var app = AttachEndpoint.Build(new AttachedServer(engine, authenticator), new AttachEndpointOptions(socket, listen, web), _stop.Token);
        await app.StartAsync(Ct);
        if (socket is not null)
            AttachEndpoint.Secure(socket);
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task TheUnixSocketIsTheOwnersAndStillNeedsTheToken()
    {
        if (OperatingSystem.IsWindows())
            return;
        var socket = Path.Combine(_directory, "run", "attach.sock");
        await using var app = await StartAsync(socket, null);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(socket)!));
        Assert.True(AttachedAddress.TryParse("unix:" + socket, out var address, out _));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            AttachedClient.ConnectAsync(address, "a wrong token of enough length", "test", new InlineDispatcher(), Ct));

        await using var client = await AttachedClient.ConnectAsync(address, Owner, "test", new InlineDispatcher(), Ct);
        Assert.Equal(AttachedRole.Owner, client.Capabilities.Role);
        Assert.Contains(await client.ListAsync(Ct), summary => summary.Id == DemoSessionService.MainSessionId);
        await _stop.CancelAsync();
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task ASocketAServerLeftBehindIsReplaced()
    {
        var socket = Path.Combine(_directory, "stale.sock");
        // A socket file nobody listens on, as a killed server leaves it (bound, never listening: a connect is refused).
        using var stale = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);
        stale.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socket));
        Assert.True(File.Exists(socket));

        await using var app = await StartAsync(socket, null);
        Assert.True(AttachedAddress.TryParse("unix:" + socket, out var address, out _));
        await using var client = await AttachedClient.ConnectAsync(address, Owner, "test", new InlineDispatcher(), Ct);
        await _stop.CancelAsync();
        await app.StopAsync(Ct);
    }

    [Fact]
    public async Task OnLoopbackABrowserIsServedOnlyFromTheServersOwnPagesAndOnlyUnderLoopbackNames()
    {
        var web = Directory.CreateDirectory(Path.Combine(_directory, "web")).FullName;
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><title>Corvid</title>", Ct);
        await File.WriteAllBytesAsync(Path.Combine(web, "dotnet.native.wasm"), [0, 0x61, 0x73, 0x6d], Ct);
        await using var app = await StartAsync(null, new Uri("ws://127.0.0.1:0"), web);
        var bound = AttachEndpoint.ListeningOn(app)!;
        var socketUri = new Uri($"ws://{bound.Authority}{AttachedProtocol.Path}");

        // The app's WebAssembly build is served beside the socket, with its types.
        using var http = new HttpClient { BaseAddress = new Uri($"http://{bound.Authority}/") };
        Assert.Contains("<title>Corvid</title>", await http.GetStringAsync("", Ct), StringComparison.Ordinal);
        using var wasm = await http.GetAsync("dotnet.native.wasm", Ct);
        Assert.Equal("application/wasm", wasm.Content.Headers.ContentType?.MediaType);

        // Another site open in the same browser cannot open the socket.
        using (var foreign = new ClientWebSocket())
        {
            foreign.Options.SetRequestHeader("Origin", "http://evil.example");
            var refused = await Assert.ThrowsAsync<WebSocketException>(() => foreign.ConnectAsync(socketUri, Ct));
            Assert.Contains("403", refused.Message, StringComparison.Ordinal);
        }
        // A name that resolves to loopback by rebinding is refused too.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "index.html"))
        {
            request.Headers.Host = "attacker.example";
            using var rebound = await http.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.MisdirectedRequest, rebound.StatusCode);
        }

        // The server's own page connects, and still needs the token.
        using var own = new ClientWebSocket();
        own.Options.SetRequestHeader("Origin", $"http://{bound.Authority}");
        await own.ConnectAsync(socketUri, Ct);
        var channel = new WebSocketMessageChannel(own, AttachedProtocol.MaxResponseBytes);
        await using var client = await RemoteSessionService.ConnectAsync(channel, Owner, "browser", new InlineDispatcher(), Ct);
        Assert.NotEmpty(await client.ListAsync(Ct));
        await _stop.CancelAsync();
        await app.StopAsync(Ct);
    }

    // A6 review L1: beyond loopback, only the address listened on is served (a DNS name could be rebound onto this host,
    // and its pages would then pass the Origin check as this server's own); on an unspecified address, any IP literal.
    [Theory]
    [InlineData("ws://192.0.2.5:8737", "192.0.2.5:8737", true)]
    [InlineData("ws://192.0.2.5:8737", "attacker.example:8737", false)]
    [InlineData("ws://192.0.2.5:8737", "192.0.2.6:8737", false)]
    [InlineData("ws://0.0.0.0:8737", "192.0.2.6:8737", true)]
    [InlineData("ws://0.0.0.0:8737", "corvid.lan:8737", false)]
    [InlineData("ws://[::]:8737", "[2001:db8::1]:8737", true)]
    public void BeyondLoopbackOnlyTheAddressListenedOnIsServed(string listen, string host, bool served)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.99");
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        var refusal = AttachEndpoint.Refusal(context, loopback: false, new Uri(listen));
        Assert.Equal(served, refusal is null);
        if (!served)
            Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status421MisdirectedRequest, refusal!.Status);
    }

    // A6 review L2: a socket directory someone else may write, or that is not the user's, is refused before the bind.
    [Fact]
    public void ASocketDirectoryOthersMayWriteOrThatIsNotYoursIsRefused()
    {
        if (OperatingSystem.IsWindows())
            return;
        var own = Directory.CreateDirectory(Path.Combine(_directory, "own")).FullName;
        File.SetUnixFileMode(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        AttachEndpoint.CheckSocketDirectory(own);

        File.SetUnixFileMode(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        Assert.Contains("written by others", Assert.Throws<IOException>(() => AttachEndpoint.CheckSocketDirectory(own)).Message, StringComparison.Ordinal);
        // /tmp: anyone may write it.
        Assert.Throws<IOException>(() => AttachEndpoint.CheckSocketDirectory("/tmp"));
        // A directory root owns (unless the tests run as root).
        if (UnixPeers.CurrentUser is not (null or 0))
            Assert.Contains("not yours", Assert.Throws<IOException>(() => AttachEndpoint.CheckSocketDirectory("/usr")).Message, StringComparison.Ordinal);
    }

    // A6 review L7: the first SIGTERM stops serve as Ctrl-C does; a second one is not swallowed, so the process ends.
    [Fact]
    public void ASecondSigtermIsNotSwallowed()
    {
        using var stop = new CancellationTokenSource();
        var first = new System.Runtime.InteropServices.PosixSignalContext(System.Runtime.InteropServices.PosixSignal.SIGTERM);
        ServeCommand.OnTerminate(first, stop);
        Assert.True(first.Cancel);
        Assert.True(stop.IsCancellationRequested);

        var second = new System.Runtime.InteropServices.PosixSignalContext(System.Runtime.InteropServices.PosixSignal.SIGTERM);
        ServeCommand.OnTerminate(second, stop);
        Assert.False(second.Cancel);
    }

    // A6 review H2: the protection side (Corvid.Sandbox, for the approvals and the launcher) and attached mode (Corvid.App.Core)
    // name the same directory.
    [Fact]
    public void TheProtectedAttachDirectoryIsTheOneAttachedModeUses() =>
        Assert.Contains(AttachedPaths.StateDirectory, Corvid.Sandbox.BuiltInDenials.AttachStateDirectories(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable));

    [Theory]
    [InlineData("ws://127.0.0.1:8737", true)]
    [InlineData("127.0.0.1:8737", true)]
    [InlineData("ws://[::1]:8737", true)]
    [InlineData("ws://localhost:8737", true)]
    [InlineData("ws://example.com:8737", false)]
    [InlineData("ws://127.0.0.1:8737/other", false)]
    [InlineData("ftp://127.0.0.1:8737", false)]
    public void ListenAddressesAreIpAddressesOrLocalhost(string text, bool ok) =>
        Assert.Equal(ok, AttachEndpoint.TryParseListen(text, out _, out _));
}
