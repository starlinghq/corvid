using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Remote;

/// <summary>
/// Where an app attaches (G-1): a WebSocket address (<c>ws://127.0.0.1:8737/attach</c>, <c>wss://…</c>) or, from the
/// desktop, a Unix socket (<c>unix:/run/user/1000/corvid/attach.sock</c>).
/// </summary>
public sealed record AttachedAddress(Uri WebSocket, string? UnixSocket)
{
    /// <summary>The address as the person gave it, for the status bar and messages.</summary>
    public string Display => UnixSocket is { } path ? "unix:" + path : WebSocket.ToString();

    /// <summary>Whether the address is this machine's own: a Unix socket or a loopback host.</summary>
    public bool IsLocal => UnixSocket is not null || WebSocket.IsLoopback
        || (IPAddress.TryParse(WebSocket.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Reads <c>ws://host:port[/attach]</c>, <c>wss://…</c>, <c>host:port</c> or <c>unix:/path</c>.</summary>
    public static bool TryParse(string? text, out AttachedAddress address, out string? error)
    {
        address = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "--attach needs an address: ws://127.0.0.1:8737 or unix:/path/to/attach.sock.";
            return false;
        }
        text = text.Trim();
        if (text.StartsWith("unix:", StringComparison.Ordinal))
        {
            var path = text["unix:".Length..];
            if (!Path.IsPathRooted(path))
            {
                error = "A Unix socket address needs an absolute path (unix:/run/user/1000/corvid/attach.sock).";
                return false;
            }
            address = new AttachedAddress(new Uri("ws://localhost" + AttachedProtocol.Path), path);
            return true;
        }
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "ws://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || uri.Host.Length == 0
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = $"'{SafeText.Clean(text)}' is not an attach address: ws://host:port, wss://host:port or unix:/path.";
            return false;
        }
        if (uri.AbsolutePath is "" or "/")
            uri = new UriBuilder(uri) { Path = AttachedProtocol.Path }.Uri;
        address = new AttachedAddress(uri, null);
        return true;
    }
}

/// <summary>Opens attached mode's WebSocket (G-1) from the desktop or a browser.</summary>
public static class AttachedClient
{
    /// <summary>Connects to the address; the channel reads at most <see cref="AttachedProtocol.MaxResponseBytes"/> per message.</summary>
    public static async Task<IMessageChannel> OpenAsync(AttachedAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        var socket = new ClientWebSocket();
        try
        {
            // A browser keeps its WebSockets alive itself and refuses the option.
            if (!OperatingSystem.IsBrowser())
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            if (address.UnixSocket is { } path)
            {
                if (OperatingSystem.IsBrowser())
                    throw new PlatformNotSupportedException("A browser attaches over a WebSocket address, not a Unix socket.");
                using var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, token) =>
                    {
                        var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try
                        {
                            await unix.ConnectAsync(new UnixDomainSocketEndPoint(path), token).ConfigureAwait(false);
                            return new NetworkStream(unix, ownsSocket: true);
                        }
                        catch
                        {
                            unix.Dispose();
                            throw;
                        }
                    },
                };
                using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
                await socket.ConnectAsync(address.WebSocket, invoker, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(address.WebSocket, cancellationToken).ConfigureAwait(false);
            }
            return new WebSocketMessageChannel(socket, AttachedProtocol.MaxResponseBytes);
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or SocketException)
        {
            socket.Dispose();
            throw new IOException($"Cannot reach Corvid at {address.Display}: {exception.Message}", exception);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Pairs this device with a code from <c>corvid serve pair</c>, returning the device token to keep.</summary>
    public static async Task<string> PairAsync(AttachedAddress address, string code, string device, CancellationToken cancellationToken)
    {
        var channel = await OpenAsync(address, cancellationToken).ConfigureAwait(false);
        return await RemoteSessionService.PairAsync(channel, code, device, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Connects and authenticates with a token.</summary>
    public static async Task<RemoteSessionService> ConnectAsync(AttachedAddress address, string token, string device, Mvvm.IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var channel = await OpenAsync(address, cancellationToken).ConfigureAwait(false);
        return await RemoteSessionService.ConnectAsync(channel, token, device, dispatcher, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The app's services when it is attached to a Corvid elsewhere (G-1): the sessions are that engine's, over the wire; every
/// other area says it is on the engine's machine, since attached mode serves the session seam (A6). The Sessions page, the
/// rail, the attention tray, approvals and questions all work as in-process.
/// </summary>
public sealed class RemoteAppServices(RemoteSessionService sessions, AttachedAddress address)
    : UnavailableAppServices($"Attached to Corvid at {address.Display}: this window shows its sessions; this area runs on that machine " +
        "(the app or corvid there).")
{
    public RemoteSessionService Sessions { get; } = sessions;

    public AttachedAddress Address { get; } = address;

    public override ISessionService CreateSessionService(AppComposition app) => new Borrowed(Sessions);

    /// <summary>The composition disposes what it is given; the connection is the head's to close, so the composition gets a view of it.</summary>
    private sealed class Borrowed(RemoteSessionService inner) : ISessionService
    {
        public event EventHandler<SessionSummary>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public event EventHandler? ListChanged
        {
            add => inner.ListChanged += value;
            remove => inner.ListChanged -= value;
        }

        public bool HasComposerModes => inner.HasComposerModes;

        public bool CanSteer => inner.CanSteer;

        public bool CanPin => inner.CanPin;

        public bool CanRecap => inner.CanRecap;

        public bool CanCreate => inner.CanCreate;

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);

        public Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default) =>
            inner.OpenAsync(sessionId, onUpdate, cancellationToken);

        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(sessionId, text, cancellationToken);

        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
            inner.QueueAsync(sessionId, text, cancellationToken);

        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) =>
            inner.RemoveQueuedAsync(sessionId, queuedId, cancellationToken);

        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) =>
            inner.MoveQueuedAsync(sessionId, queuedId, index, cancellationToken);

        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) =>
            inner.TakeBackQueuedAsync(sessionId, queuedId, cancellationToken);

        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SteerAsync(sessionId, text, cancellationToken);

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InterruptAsync(sessionId, cancellationToken);

        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) =>
            inner.AnswerApprovalAsync(sessionId, approvalId, answer, cancellationToken);

        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) =>
            inner.AnswerQuestionAsync(sessionId, questionId, answer, cancellationToken);

        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) =>
            inner.RunCommandAsync(sessionId, line, cancellationToken);

        public Task SetApprovalModeAsync(string sessionId, Shell.ApprovalMode mode, CancellationToken cancellationToken = default) =>
            inner.SetApprovalModeAsync(sessionId, mode, cancellationToken);

        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => inner.SetGoalAsync(sessionId, goal, cancellationToken);

        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => inner.CompactAsync(sessionId, focus, cancellationToken);

        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) =>
            inner.ForkAsync(sessionId, throughPosition, cancellationToken);

        public Task<bool> StopChildAsync(string childId, CancellationToken cancellationToken = default) => inner.StopChildAsync(childId, cancellationToken);

        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => inner.RenameAsync(sessionId, title, cancellationToken);

        public Task<SessionDraft?> LoadDraftAsync(string sessionId, CancellationToken cancellationToken = default) => inner.LoadDraftAsync(sessionId, cancellationToken);

        public Task SaveDraftAsync(string sessionId, SessionDraft draft, CancellationToken cancellationToken = default) =>
            inner.SaveDraftAsync(sessionId, draft, cancellationToken);

        public Task SetPinnedAsync(string sessionId, bool pinned, CancellationToken cancellationToken = default) =>
            inner.SetPinnedAsync(sessionId, pinned, cancellationToken);

        public Task ViewedAsync(string sessionId, bool viewing, CancellationToken cancellationToken = default) =>
            inner.ViewedAsync(sessionId, viewing, cancellationToken);

        public Task RecapAsync(string sessionId, CancellationToken cancellationToken = default) => inner.RecapAsync(sessionId, cancellationToken);

        public Task<string> CreateAsync(CancellationToken cancellationToken = default) => inner.CreateAsync(cancellationToken);
    }
}
