using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Remote;

/// <summary>Who an accepted token or redeemed code belongs to.</summary>
/// <param name="Device">The paired device's name; null for the owner.</param>
public sealed record AttachedIdentity(AttachedRole Role, string? Device);

/// <summary>Checks a connection's credentials (G-1): the owner token, a paired device's token, or a pairing code.</summary>
public interface IAttachedAuthenticator
{
    /// <summary>Who holds the token, or null when it is refused (or cannot be checked: Vault is not answering).</summary>
    ValueTask<AttachedIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken);

    /// <summary>
    /// Redeems a pairing code the owner made on the machine for a new device token, once; null when the code is wrong, used
    /// or expired, or too many attempts failed lately.
    /// </summary>
    ValueTask<(string Token, AttachedIdentity Identity)?> PairAsync(string code, string device, CancellationToken cancellationToken);
}

/// <summary>
/// Serves a session service (the engine's, in <c>corvid serve --attach</c>) to attached apps over
/// <see cref="AttachedProtocol"/>, one connection at a time per call of <see cref="ServeAsync"/>. Every connection
/// authenticates first (the owner token or a paired device's; a code pairs a device), within
/// <see cref="AttachedProtocol.AuthenticationWindow"/>; until then nothing of a session is sent and nothing is asked. After,
/// it gets the seam: the list and its changes, sessions opened with their history and live updates, and every request,
/// answers to approvals and questions included, which reach the engine's turn as the window's own do.
/// <para>
/// Everything a connection sends is untrusted: methods outside <see cref="AttachedProtocol.SessionMethods"/> are refused,
/// ids and texts are bounded, and the engine's own checks (an answer to a closed prompt changes nothing, hidden characters in
/// a narrowed rule refuse it) apply as for the window. A connection that drops leaves the engine as it was: its sessions are
/// closed, an approval it did not answer waits for another client or its deadline, and once no client is left a question
/// asked inside a turn is put aside (<see cref="NoClientsLeft"/>), so no turn waits on a client that went.
/// </para>
/// </summary>
public sealed class AttachedServer(ISessionService sessions, IAttachedAuthenticator authenticator, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ISessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IAttachedAuthenticator _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
    private int _clients;

    /// <summary>Authenticated connections now.</summary>
    public int Clients => Volatile.Read(ref _clients);

    /// <summary>Where the server writes a line about each connection (never a token, a code or a session's text).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// The last authenticated connection went: the engine puts aside the questions its turns wait on inside the turn (they
    /// stay open for the next message), so a turn never waits forever on a client that dropped. Approvals keep their deadline.
    /// </summary>
    public Action? NoClientsLeft { get; init; }

    /// <summary>The host name the server gives itself in <see cref="AttachedProtocol.Initialize"/>.</summary>
    public string HostName { get; init; } = Environment.MachineName;

    /// <summary>Serves one connection until it closes or <paramref name="cancellationToken"/> stops the server.</summary>
    public async Task ServeAsync(IMessageChannel channel, string peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var connection = new Connection(this, peer);
        var rpc = new JsonRpcPeer(channel, connection.HandleAsync, onNotification: null);
        connection.Peer = rpc;
        await using (rpc.ConfigureAwait(false))
        {
            rpc.Start();
            using var stop = cancellationToken.Register(() => _ = rpc.CloseAsync("the server is stopping").AsTask());
            using var window = new CancellationTokenSource(AttachedProtocol.AuthenticationWindow, _time);
            using var late = window.Token.Register(() =>
            {
                if (!connection.Authenticated)
                    _ = rpc.CloseAsync("not authenticated in time").AsTask();
            });
            try
            {
                await rpc.Closed.ConfigureAwait(false);
            }
            finally
            {
                connection.Close();
            }
        }
    }

    private void Joined(Connection connection)
    {
        Interlocked.Increment(ref _clients);
        _sessions.SessionChanged += connection.OnSessionChanged;
        _sessions.ListChanged += connection.OnListChanged;
        Log?.Invoke($"attach: {connection.Who} connected from {connection.From}");
    }

    private void Left(Connection connection)
    {
        _sessions.SessionChanged -= connection.OnSessionChanged;
        _sessions.ListChanged -= connection.OnListChanged;
        Log?.Invoke($"attach: {connection.Who} disconnected");
        if (Interlocked.Decrement(ref _clients) == 0)
            NoClientsLeft?.Invoke();
    }

    private sealed class Connection(AttachedServer server, string from)
    {
        private readonly ConcurrentDictionary<string, Opened> _open = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _viewing = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private int _authenticating;
        private volatile bool _authenticated;
        private bool _closed;

        public JsonRpcPeer Peer { get; set; } = null!;

        public bool Authenticated => _authenticated;

        public string From { get; } = from;

        public string Who { get; private set; } = "a client";

        private ISessionService Sessions => server._sessions;

        public void OnSessionChanged(object? sender, SessionSummary summary) =>
            Peer.Notify(AttachedProtocol.SessionChanged, new JsonObject { ["summary"] = JsonSerializer.SerializeToNode(summary, AttachedJson.Summary) });

        public void OnListChanged(object? sender, EventArgs e) => Peer.Notify(AttachedProtocol.ListChanged, new JsonObject());

        public void Close()
        {
            bool wasIn;
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                wasIn = _authenticated;
            }
            foreach (var opened in _open.Values)
                opened.Subscription?.Dispose();
            _open.Clear();
            foreach (var id in _viewing.Keys)
                _ = Quietly(Sessions.ViewedAsync(id, viewing: false));
            _viewing.Clear();
            if (wasIn)
                server.Left(this);
        }

        public async Task<JsonNode?> HandleAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        {
            if (!_authenticated)
                return await AuthenticateAsync(method, parameters, cancellationToken).ConfigureAwait(false);
            if (!AttachedProtocol.SessionMethods.Contains(method))
                throw new JsonRpcException(AttachedProtocol.Errors.MethodNotFound, "No such method in attached mode.");
            var p = new Params(parameters);
            switch (method)
            {
                case AttachedProtocol.List:
                    return JsonSerializer.SerializeToNode([.. await Sessions.ListAsync(cancellationToken).ConfigureAwait(false)], AttachedJson.Summaries);
                case AttachedProtocol.New:
                    return new JsonObject { ["sessionId"] = await Sessions.CreateAsync(cancellationToken).ConfigureAwait(false) };
                case AttachedProtocol.Load:
                    return await LoadAsync(p.Id("sessionId"), p.Id("subscription"), cancellationToken).ConfigureAwait(false);
                case AttachedProtocol.Close:
                    if (_open.TryRemove(p.Id("subscription"), out var closing))
                        closing.Subscription?.Dispose();
                    return null;
                case AttachedProtocol.Prompt:
                    await Sessions.SendAsync(p.Id("sessionId"), p.Text("text"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Queue:
                    return JsonSerializer.SerializeToNode(await Sessions.QueueAsync(p.Id("sessionId"), p.Text("text"), cancellationToken).ConfigureAwait(false),
                        AttachedJson.Queued);
                case AttachedProtocol.QueueRemove:
                    await Sessions.RemoveQueuedAsync(p.Id("sessionId"), p.Id("queuedId"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.QueueMove:
                    await Sessions.MoveQueuedAsync(p.Id("sessionId"), p.Id("queuedId"), p.Int("index"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.QueueTakeBack:
                    return new JsonObject { ["text"] = await Sessions.TakeBackQueuedAsync(p.Id("sessionId"), p.Id("queuedId"), cancellationToken).ConfigureAwait(false) };
                case AttachedProtocol.Steer:
                    await Sessions.SteerAsync(p.Id("sessionId"), p.Text("text"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Cancel:
                    await Sessions.InterruptAsync(p.Id("sessionId"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.AnswerApproval:
                    await Sessions.AnswerApprovalAsync(p.Id("sessionId"), p.Id("approvalId"), p.Answer(), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.AnswerQuestion:
                    await Sessions.AnswerQuestionAsync(p.Id("sessionId"), p.Id("questionId"), p.Text("answer"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Command:
                    await Sessions.RunCommandAsync(p.Id("sessionId"), p.Text("line"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.SetMode:
                    await Sessions.SetApprovalModeAsync(p.Id("sessionId"), p.Mode(), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.SetGoal:
                    await Sessions.SetGoalAsync(p.Id("sessionId"), p.OptionalText("goal"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Compact:
                    await Sessions.CompactAsync(p.Id("sessionId"), p.OptionalText("focus"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Fork:
                    return new JsonObject
                    {
                        ["sessionId"] = await Sessions.ForkAsync(p.Id("sessionId"), p.OptionalLong("position"), cancellationToken).ConfigureAwait(false),
                    };
                case AttachedProtocol.StopChild:
                    return new JsonObject { ["stopped"] = await Sessions.StopChildAsync(p.Id("childId"), cancellationToken).ConfigureAwait(false) };
                case AttachedProtocol.Rename:
                    await Sessions.RenameAsync(p.Id("sessionId"), p.Text("title"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.LoadDraft:
                    return await Sessions.LoadDraftAsync(p.Id("sessionId"), cancellationToken).ConfigureAwait(false) is { } draft
                        ? JsonSerializer.SerializeToNode(draft, AttachedJson.Draft)
                        : null;
                case AttachedProtocol.SaveDraft:
                    await Sessions.SaveDraftAsync(p.Id("sessionId"), p.Draft(), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.SetPinned:
                    await Sessions.SetPinnedAsync(p.Id("sessionId"), p.Bool("pinned"), cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Viewed:
                    var viewed = p.Id("sessionId");
                    var viewing = p.Bool("viewing");
                    if (viewing)
                        _viewing[viewed] = 0;
                    else
                        _viewing.TryRemove(viewed, out _);
                    await Sessions.ViewedAsync(viewed, viewing, cancellationToken).ConfigureAwait(false);
                    return null;
                case AttachedProtocol.Recap:
                    await Sessions.RecapAsync(p.Id("sessionId"), cancellationToken).ConfigureAwait(false);
                    return null;
                default:
                    throw new JsonRpcException(AttachedProtocol.Errors.MethodNotFound, "No such method in attached mode.");
            }
        }

        private async Task<JsonNode?> AuthenticateAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
        {
            if (method is not (AttachedProtocol.Initialize or AttachedProtocol.Pair) || Interlocked.Exchange(ref _authenticating, 1) == 1)
                throw Refuse("authenticate first, once");
            var p = new Params(parameters);
            if (parameters?["protocolVersion"] is not JsonValue version || !version.TryGetValue<int>(out var v) || v != AttachedProtocol.Version)
                throw Refuse($"this Corvid speaks attached protocol {AttachedProtocol.Version}");
            var device = Name(p.OptionalText("device"));
            AttachedIdentity? identity;
            string? token = null;
            if (method == AttachedProtocol.Pair)
            {
                var paired = await server._authenticator.PairAsync(p.Text("code", 64), device, cancellationToken).ConfigureAwait(false);
                identity = paired?.Identity;
                token = paired?.Token;
            }
            else
            {
                identity = await server._authenticator.AuthenticateAsync(p.Text("token", 4096), cancellationToken).ConfigureAwait(false);
            }
            if (identity is null)
            {
                server.Log?.Invoke($"attach: refused {(method == AttachedProtocol.Pair ? "a pairing code" : "a token")} from {From}");
                throw Refuse(method == AttachedProtocol.Pair ? "that pairing code is wrong, used or expired" : "that token is refused");
            }
            if (token is not null)
            {
                // A pairing connection hands over the device's token and nothing else: it stays unauthenticated, and the
                // client closes it (or the authentication window does). The device then connects with its token.
                server.Log?.Invoke($"attach: paired device '{identity.Device}' from {From}");
                return new JsonObject { ["token"] = token, ["device"] = identity.Device };
            }
            lock (_gate)
            {
                if (_closed)
                    throw new JsonRpcException(AttachedProtocol.Errors.Unauthorized, "closed");
                _authenticated = true;
                Who = identity.Role == AttachedRole.Owner ? "the owner" : $"device '{identity.Device}'";
            }
            server.Joined(this);
            return new JsonObject
            {
                ["protocolVersion"] = AttachedProtocol.Version,
                ["role"] = identity.Role == AttachedRole.Owner ? "owner" : "device",
                ["device"] = identity.Device,
                ["host"] = server.HostName,
                ["capabilities"] = new JsonObject
                {
                    ["canCreate"] = Sessions.CanCreate,
                    ["canSteer"] = Sessions.CanSteer,
                    ["canPin"] = Sessions.CanPin,
                    ["canRecap"] = Sessions.CanRecap,
                    ["hasComposerModes"] = Sessions.HasComposerModes,
                },
            };
        }

        /// <summary>The refusal to answer with; the connection closes once it is sent.</summary>
        private static JsonRpcException Refuse(string reason) => new(AttachedProtocol.Errors.Unauthorized, reason) { Fatal = true };

        private async Task<JsonNode?> LoadAsync(string sessionId, string subscriptionId, CancellationToken cancellationToken)
        {
            var opened = new Opened(subscriptionId);
            if (_open.Count >= AttachedProtocol.MaxSubscriptions)
                throw new JsonRpcException(AttachedProtocol.Errors.Busy, $"At most {AttachedProtocol.MaxSubscriptions} sessions open at once.");
            if (!_open.TryAdd(subscriptionId, opened))
                throw new JsonRpcException(AttachedProtocol.Errors.InvalidParams, "That subscription id is in use.");
            try
            {
                // Updates go out as they come, after the history's answer or before it: the client keeps the early ones for after it.
                var subscription = await Sessions.OpenAsync(sessionId, update => Send(opened, update), cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_closed || !_open.ContainsKey(subscriptionId))
                    {
                        subscription.Dispose();
                        throw new OperationCanceledException("The subscription was closed while it opened.");
                    }
                    opened.Subscription = subscription;
                }
                return new JsonObject
                {
                    ["subscription"] = subscriptionId,
                    ["history"] = JsonSerializer.SerializeToNode([.. subscription.History], AttachedJson.Updates),
                };
            }
            catch
            {
                _open.TryRemove(subscriptionId, out _);
                throw;
            }
        }

        private void Send(Opened opened, SessionUpdate update)
        {
            if (!_open.ContainsKey(opened.Id))
                return;
            JsonNode? node;
            try
            {
                node = JsonSerializer.SerializeToNode(update, AttachedJson.Update);
            }
            catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException)
            {
                server.Log?.Invoke($"attach: an update of kind {update.GetType().Name} cannot be sent: {exception.Message}");
                return;
            }
            Peer.Notify(AttachedJson.IsAcpKind(update.GetType()) ? AttachedProtocol.SessionUpdate : AttachedProtocol.CorvidUpdate,
                new JsonObject { ["subscription"] = opened.Id, ["update"] = node });
        }

        private static string Name(string? device)
        {
            var clean = SafeText.Clean(device ?? "").Trim();
            return clean.Length == 0 ? "a device" : clean.Length > 64 ? clean[..64] : clean;
        }

        private static async Task Quietly(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }
    }

    private sealed class Opened(string id)
    {
        public string Id { get; } = id;

        public ISessionSubscription? Subscription { get; set; }
    }

    /// <summary>A request's parameters, read strictly: a missing, mistyped or oversized one refuses the request.</summary>
    private readonly struct Params(JsonObject? parameters)
    {
        private const int MaxText = 512 * 1024;

        public string Id(string name)
        {
            var id = Text(name, AttachedProtocol.MaxIdLength);
            return id.Length > 0 ? id : throw Invalid(name);
        }

        public string Text(string name, int max = MaxText) =>
            parameters?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length <= max ? text : throw Invalid(name);

        public string? OptionalText(string name) => parameters?[name] is null ? null : Text(name);

        public int Int(string name) =>
            parameters?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : throw Invalid(name);

        public long? OptionalLong(string name) =>
            parameters?[name] is null ? null : parameters[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : throw Invalid(name);

        public bool Bool(string name) =>
            parameters?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : throw Invalid(name);

        public ApprovalMode Mode() =>
            Enum.TryParse<ApprovalMode>(Text("mode", 32), ignoreCase: false, out var mode) && Enum.IsDefined(mode) ? mode : throw Invalid("mode");

        public ApprovalAnswer Answer()
        {
            var answer = Read(parameters?["answer"], AttachedJson.Answer, "answer");
            if (!Enum.IsDefined(answer.Choice) || answer.Reason is { Length: > MaxText } || answer.RulePaths.IsDefault
                || answer.RulePaths.Length > 64 || answer.RulePaths.Any(paths => paths.IsDefault || paths.Length > 256))
                throw Invalid("answer");
            return answer;
        }

        public SessionDraft Draft()
        {
            var draft = Read(parameters?["draft"], AttachedJson.Draft, "draft");
            return draft.Text is null || draft.Text.Length > MaxText || draft.Stash is { Length: > MaxText } ? throw Invalid("draft") : draft;
        }

        private static T Read<T>(JsonNode? node, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, string name)
        {
            try
            {
                return node is null ? throw Invalid(name) : node.Deserialize(info) ?? throw Invalid(name);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                throw Invalid(name);
            }
        }

        private static JsonRpcException Invalid(string name) =>
            new(AttachedProtocol.Errors.InvalidParams, $"The request's '{name}' is missing or not valid.");
    }
}
