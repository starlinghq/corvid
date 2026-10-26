using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Remote;

/// <summary>What the attached engine said of itself when the connection was made.</summary>
/// <param name="Role">Whether this client holds the owner token or is a paired device.</param>
/// <param name="Device">The paired device's name; null for the owner.</param>
public sealed record AttachedCapabilities(AttachedRole Role, string? Device)
{
    public bool CanCreate { get; init; }

    public bool CanSteer { get; init; }

    public bool CanPin { get; init; }

    public bool CanRecap { get; init; }

    public bool HasComposerModes { get; init; }

    /// <summary>Where the attached Corvid runs, as it names itself (its host name); untrusted text.</summary>
    public string? Host { get; init; }
}

/// <summary>
/// The session seam over attached mode's wire (G-1): every request goes to the engine on the other end, and its updates come
/// back on the dispatcher in order, so the Sessions page, the rail, the tray and the Board draw a remote engine exactly as
/// they draw the in-process one. Approvals and questions are the engine's own cards, answered from here into the engine's
/// turn. Nothing it reads is trusted beyond its shape: error texts are cleaned of hidden characters, and the cards keep
/// their text as the engine sent it, so the person approving sees it revealed as in-process.
/// <para>
/// When the connection ends, requests fail with an <see cref="IOException"/> saying so, the engine's turn goes on (its
/// approvals fall back to their deadline there, its in-turn questions are put aside), and <see cref="Disconnected"/> is raised.
/// </para>
/// </summary>
public sealed class RemoteSessionService : ISessionService, IAsyncDisposable, IDisposable
{
    private readonly JsonRpcPeer _peer;
    private readonly IDispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private long _nextSubscription;
    private volatile bool _disposed;

    private RemoteSessionService(IMessageChannel channel, IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _peer = new JsonRpcPeer(channel, onRequest: null, OnNotification);
        _ = WatchAsync();
    }

    /// <summary>What the engine said of itself at <see cref="Initialize"/>.</summary>
    public AttachedCapabilities Capabilities { get; private set; } = new(AttachedRole.Device, null);

    public event EventHandler<SessionSummary>? SessionChanged;

    public event EventHandler? ListChanged;

    /// <summary>The connection ended (the engine stopped, the network went, the token was revoked). Raised on the dispatcher.</summary>
    public event EventHandler<string>? Disconnected;

    public bool IsConnected => !_peer.IsClosed;

    public bool HasComposerModes => Capabilities.HasComposerModes;

    public bool CanSteer => Capabilities.CanSteer;

    public bool CanPin => Capabilities.CanPin;

    public bool CanRecap => Capabilities.CanRecap;

    public bool CanCreate => Capabilities.CanCreate;

    /// <summary>Authenticates with a token (the owner's, or a paired device's) and returns the service.</summary>
    /// <exception cref="UnauthorizedAccessException">The engine refused the token.</exception>
    /// <exception cref="IOException">The connection ended first.</exception>
    public static async Task<RemoteSessionService> ConnectAsync(IMessageChannel channel, string token, string device, IDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(dispatcher);
        var service = new RemoteSessionService(channel, dispatcher);
        try
        {
            var result = await service.CallAsync(AttachedProtocol.Initialize, new JsonObject
            {
                ["protocolVersion"] = AttachedProtocol.Version,
                ["token"] = token,
                ["device"] = device,
            }, cancellationToken).ConfigureAwait(false);
            service.Capabilities = ReadCapabilities(result as JsonObject);
            return service;
        }
        catch (IOException exception) when (channel.ClosedBecause is { Length: > 0 } reason)
        {
            // The engine closed the connection with its reason before its refusal's answer was read (a busy machine): a
            // close before the start is answered is a refusal, and says why.
            await service.DisposeAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException("The attached Corvid refused this connection: " + SafeText.Clean(reason), exception);
        }
        catch
        {
            await service.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Pairs this device with a code the owner made on the engine's machine (<c>corvid serve pair</c>) and returns the
    /// device's token, which the caller keeps for <see cref="ConnectAsync"/>. The connection is closed afterwards.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The code was wrong, used or expired.</exception>
    public static async Task<string> PairAsync(IMessageChannel channel, string code, string device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var peer = new JsonRpcPeer(channel, null, null);
        await using (peer.ConfigureAwait(false))
        {
            peer.Start();
            try
            {
                var result = await peer.RequestAsync(AttachedProtocol.Pair, new JsonObject
                {
                    ["protocolVersion"] = AttachedProtocol.Version,
                    ["code"] = code,
                    ["device"] = device,
                }, cancellationToken).ConfigureAwait(false);
                return (result as JsonObject)?["token"] is JsonValue value && value.TryGetValue<string>(out var token) && token.Length > 0
                    ? token
                    : throw new IOException("The attached Corvid did not return a device token.");
            }
            catch (JsonRpcException error)
            {
                throw JsonRpcPeer.Raise(error);
            }
            catch (IOException exception) when (channel.ClosedBecause is { Length: > 0 } reason)
            {
                throw new UnauthorizedAccessException("The attached Corvid refused the code: " + SafeText.Clean(reason), exception);
            }
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.List, null, cancellationToken).ConfigureAwait(false);
        return Read(result, AttachedJson.Summaries) ?? [];
    }

    public async Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(onUpdate);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = "s" + Interlocked.Increment(ref _nextSubscription);
        var subscription = new Subscription(this, id, sessionId, onUpdate);
        // Registered before the request, so updates that arrive before the result are kept for after the history.
        _subscriptions[id] = subscription;
        try
        {
            var result = await CallAsync(AttachedProtocol.Load, new JsonObject { ["sessionId"] = sessionId, ["subscription"] = id },
                cancellationToken).ConfigureAwait(false);
            var history = (result as JsonObject)?["history"] is { } node ? Read(node, AttachedJson.Updates) ?? [] : [];
            subscription.Begin(history);
            return subscription;
        }
        catch
        {
            _subscriptions.TryRemove(id, out _);
            throw;
        }
    }

    public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Prompt, new JsonObject { ["sessionId"] = sessionId, ["text"] = text }, cancellationToken);

    public async Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.Queue, new JsonObject { ["sessionId"] = sessionId, ["text"] = text }, cancellationToken)
            .ConfigureAwait(false);
        return Read(result, AttachedJson.Queued) ?? throw new IOException("The attached Corvid did not return the queued message.");
    }

    public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.QueueRemove, new JsonObject { ["sessionId"] = sessionId, ["queuedId"] = queuedId }, cancellationToken);

    public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.QueueMove, new JsonObject { ["sessionId"] = sessionId, ["queuedId"] = queuedId, ["index"] = index },
            cancellationToken);

    public async Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.QueueTakeBack, new JsonObject { ["sessionId"] = sessionId, ["queuedId"] = queuedId },
            cancellationToken).ConfigureAwait(false);
        return (result as JsonObject)?["text"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Steer, new JsonObject { ["sessionId"] = sessionId, ["text"] = text }, cancellationToken);

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Cancel, new JsonObject { ["sessionId"] = sessionId }, cancellationToken);

    public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return CallAsync(AttachedProtocol.AnswerApproval, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["approvalId"] = approvalId,
            ["answer"] = JsonSerializer.SerializeToNode(answer, AttachedJson.Answer),
        }, cancellationToken);
    }

    public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.AnswerQuestion, new JsonObject { ["sessionId"] = sessionId, ["questionId"] = questionId, ["answer"] = answer },
            cancellationToken);

    public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Command, new JsonObject { ["sessionId"] = sessionId, ["line"] = line }, cancellationToken);

    public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.SetMode, new JsonObject { ["sessionId"] = sessionId, ["mode"] = mode.ToString() }, cancellationToken);

    public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.SetGoal, new JsonObject { ["sessionId"] = sessionId, ["goal"] = goal }, cancellationToken);

    public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Compact, new JsonObject { ["sessionId"] = sessionId, ["focus"] = focus }, cancellationToken);

    public async Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.Fork, new JsonObject { ["sessionId"] = sessionId, ["position"] = throughPosition },
            cancellationToken).ConfigureAwait(false);
        return SessionIdOf(result);
    }

    public async Task<bool> StopChildAsync(string childId, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.StopChild, new JsonObject { ["childId"] = childId }, cancellationToken).ConfigureAwait(false);
        return (result as JsonObject)?["stopped"] is JsonValue value && value.TryGetValue<bool>(out var stopped) && stopped;
    }

    public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Rename, new JsonObject { ["sessionId"] = sessionId, ["title"] = title }, cancellationToken);

    public async Task<SessionDraft?> LoadDraftAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.LoadDraft, new JsonObject { ["sessionId"] = sessionId }, cancellationToken).ConfigureAwait(false);
        return result is null ? null : Read(result, AttachedJson.Draft);
    }

    public Task SaveDraftAsync(string sessionId, SessionDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return CallAsync(AttachedProtocol.SaveDraft, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["draft"] = JsonSerializer.SerializeToNode(draft, AttachedJson.Draft),
        }, cancellationToken);
    }

    public Task SetPinnedAsync(string sessionId, bool pinned, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.SetPinned, new JsonObject { ["sessionId"] = sessionId, ["pinned"] = pinned }, cancellationToken);

    public Task ViewedAsync(string sessionId, bool viewing, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Viewed, new JsonObject { ["sessionId"] = sessionId, ["viewing"] = viewing }, cancellationToken);

    public Task RecapAsync(string sessionId, CancellationToken cancellationToken = default) =>
        CallAsync(AttachedProtocol.Recap, new JsonObject { ["sessionId"] = sessionId }, cancellationToken);

    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.New, new JsonObject(), cancellationToken).ConfigureAwait(false);
        return SessionIdOf(result);
    }

    /// <summary>The owner only: a pairing code for another device, which redeems once before <see cref="PairingCode.Expires"/>.</summary>
    /// <exception cref="UnauthorizedAccessException">This client is a paired device, not the owner.</exception>
    public async Task<PairingCode> CreatePairingCodeAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(AttachedProtocol.PairingCode, new JsonObject(), cancellationToken).ConfigureAwait(false) as JsonObject;
        return result?["code"] is JsonValue code && code.TryGetValue<string>(out var text) && text.Length is > 0 and <= 64
            && result["expires"] is JsonValue expires && expires.TryGetValue<string>(out var when)
            && DateTimeOffset.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? new PairingCode(SafeText.Clean(text), at)
            : throw new IOException("The attached Corvid did not return a pairing code.");
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _peer.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _peer.Start();
        try
        {
            return await _peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error)
        {
            throw JsonRpcPeer.Raise(error);
        }
        catch (IOException exception)
        {
            throw new IOException("The attached Corvid is not connected: " + exception.Message, exception);
        }
    }

    private static string SessionIdOf(JsonNode? result) =>
        (result as JsonObject)?["sessionId"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length is > 0 and <= AttachedProtocol.MaxIdLength
            ? id
            : throw new IOException("The attached Corvid did not return a session id.");

    private static T? Read<T>(JsonNode? node, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (node is null)
            return default;
        try
        {
            return node.Deserialize(info);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new IOException("The attached Corvid sent something this app cannot read: " + SafeText.Clean(exception.Message), exception);
        }
    }

    private static AttachedCapabilities ReadCapabilities(JsonObject? result)
    {
        bool Flag(string name) => result?["capabilities"]?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        string? Text(string name) => result?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? SafeText.Clean(text) : null;
        var role = string.Equals(Text("role"), "owner", StringComparison.Ordinal) ? AttachedRole.Owner : AttachedRole.Device;
        return new AttachedCapabilities(role, Text("device"))
        {
            CanCreate = Flag("canCreate"),
            CanSteer = Flag("canSteer"),
            CanPin = Flag("canPin"),
            CanRecap = Flag("canRecap"),
            HasComposerModes = Flag("hasComposerModes"),
            Host = Text("host"),
        };
    }

    private void OnNotification(string method, JsonObject? parameters)
    {
        switch (method)
        {
            case AttachedProtocol.SessionUpdate or AttachedProtocol.CorvidUpdate:
                if (parameters?["subscription"] is JsonValue sub && sub.TryGetValue<string>(out var id)
                    && _subscriptions.TryGetValue(id, out var subscription) && parameters["update"] is { } node)
                {
                    var update = ReadUpdate(node);
                    if (update is not null)
                        subscription.Push(update);
                }
                break;
            case AttachedProtocol.SessionChanged:
                if (parameters?["summary"] is { } summaryNode && ReadQuietly(summaryNode, AttachedJson.Summary) is { } summary)
                    _dispatcher.Post(() =>
                    {
                        if (!_disposed)
                            SessionChanged?.Invoke(this, summary);
                    });
                break;
            case AttachedProtocol.ListChanged:
                _dispatcher.Post(() =>
                {
                    if (!_disposed)
                        ListChanged?.Invoke(this, EventArgs.Empty);
                });
                break;
        }
    }

    private SessionUpdate? ReadUpdate(JsonNode node)
    {
        var update = ReadQuietly(node, AttachedJson.Update);
        if (update is null)
            // An update this app cannot read is a newer engine's: closing says so, where skipping it would draw a wrong session.
            _ = _peer.CloseAsync("an update this app cannot read").AsTask();
        return update;
    }

    private static T? ReadQuietly<T>(JsonNode node, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            return node.Deserialize(info);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return default;
        }
    }

    private async Task WatchAsync()
    {
        var reason = await _peer.Closed.ConfigureAwait(false);
        if (_disposed)
            return;
        var words = SafeText.Clean(reason ?? "the connection ended");
        // Each open session says so where the person is looking: what it shows stops here, and what they send now fails.
        foreach (var subscription in _subscriptions.Values)
            subscription.Push(new Notice(NoticeKind.Warning, "Disconnected from the attached Corvid: " + words)
            {
                Detail = "Its turns go on there; an approval nobody answers is denied at its deadline. Open the app again to attach.",
            });
        _dispatcher.Post(() =>
        {
            if (!_disposed)
                Disconnected?.Invoke(this, words);
        });
    }

    private void Close(Subscription subscription)
    {
        if (!_subscriptions.TryRemove(subscription.Id, out _) || _peer.IsClosed || _disposed)
            return;
        _ = CloseRemoteAsync(subscription.Id);
    }

    private async Task CloseRemoteAsync(string id)
    {
        try
        {
            await _peer.RequestAsync(AttachedProtocol.Close, new JsonObject { ["subscription"] = id }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonRpcException or IOException or OperationCanceledException)
        {
            // The engine closes a connection's subscriptions with it.
        }
    }

    private sealed class Subscription(RemoteSessionService service, string id, string sessionId, Action<SessionUpdate> onUpdate) : ISessionSubscription
    {
        private readonly Lock _gate = new();
        private List<SessionUpdate>? _early = [];
        private volatile bool _disposed;

        public string Id { get; } = id;

        public string SessionId { get; } = sessionId;

        public IReadOnlyList<SessionUpdate> History { get; private set; } = [];

        /// <summary>The history arrived: the updates that came before it follow it, in order.</summary>
        public void Begin(IReadOnlyList<SessionUpdate> history)
        {
            // Posted under the lock (a post only queues), so an update read meanwhile is posted after these.
            lock (_gate)
            {
                History = history;
                if (_early is { Count: > 0 } early)
                    Post(early);
                _early = null;
            }
        }

        public void Push(SessionUpdate update)
        {
            lock (_gate)
            {
                if (_early is { } early)
                    early.Add(update);
                else
                    Post([update]);
            }
        }

        private void Post(IReadOnlyList<SessionUpdate> updates) => service._dispatcher.Post(() =>
        {
            foreach (var update in updates)
            {
                if (_disposed || service._disposed)
                    return;
                onUpdate(update);
            }
        });

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            service.Close(this);
        }
    }
}
