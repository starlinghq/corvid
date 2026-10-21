using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Vault.Client;

namespace Corvid.Gateway.Slack;

/// <param name="BotSecret">The bot token (<c>xoxb-</c>), for <c>auth.test</c> and <c>chat.postMessage</c>.</param>
/// <param name="AppSecret">The app-level token (<c>xapp-</c>, scope <c>connections:write</c>), for <c>apps.connections.open</c>.</param>
public sealed record SlackOptions(SecretRef BotSecret, SecretRef AppSecret)
{
    public string Name { get; init; } = "slack";

    public Uri ApiBase { get; init; } = new("https://slack.com/api/");
}

/// <summary>
/// Slack in Socket Mode: <c>apps.connections.open</c> (app-level token) returns a one-time <c>wss://</c> URL, events arrive on
/// that WebSocket and are acknowledged at once, and answers go out with <c>chat.postMessage</c> (bot token). Both tokens are
/// Vault secrets sent as <c>Authorization: Bearer</c> placeholders through Vault's proxy. The WebSocket itself is opened
/// directly, since Vault does not relay WebSockets; its URL carries a single-use ticket, not a token. Direct messages and
/// mentions (<c>app_mention</c>) reach the gateway; other channel traffic does not.
/// </summary>
public sealed class SlackAdapter : IGatewayAdapter
{
    /// <summary>
    /// Slack truncates <c>text</c> at 40,000 characters; shorter pieces read better, and escaping (<see cref="Escape"/>) can make
    /// a piece at most five times as long.
    /// </summary>
    public const int MaxMessageLength = 7_500;

    private readonly SlackOptions _options;
    private readonly HttpClient _bot;
    private readonly HttpClient _app;
    private readonly IWebSocketConnector _sockets;
    private readonly bool _ownsClients;
    private IWebSocketConnection? _socket;
    private string _botUserId = "";

    public SlackAdapter(SlackOptions options, HttpClient bot, HttpClient app, IWebSocketConnector sockets, bool ownsClients = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bot);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(sockets);
        _options = options;
        _bot = bot;
        _app = app;
        _sockets = sockets;
        _ownsClients = ownsClients;
    }

    public static SlackAdapter Create(SlackOptions options, GatewayVault vault, IWebSocketConnector? sockets = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var timeout = TimeSpan.FromSeconds(30);
        return new SlackAdapter(options,
            GatewayHttp.Create(vault, new SecretHeader(options.BotSecret, "Authorization", "Bearer"), timeout),
            GatewayHttp.Create(vault, new SecretHeader(options.AppSecret, "Authorization", "Bearer"), timeout),
            sockets ?? new ClientWebSocketConnector(), ownsClients: true);
    }

    public string Name => _options.Name;

    public async Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        var me = await CallAsync(_bot, "auth.test", new JsonObject(), false, cancellationToken).ConfigureAwait(false);
        _botUserId = me["user_id"]?.GetValue<string>() ?? throw new InvalidDataException("auth.test returned no user id.");
        var open = await CallAsync(_app, "apps.connections.open", new JsonObject(), false, cancellationToken).ConfigureAwait(false);
        var url = open["url"]?.GetValue<string>() ?? throw new InvalidDataException("apps.connections.open returned no URL.");
        _socket = await _sockets.ConnectAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
    }

    public async Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        var socket = _socket ?? throw new InvalidOperationException("Connect first.");
        while (true)
        {
            var text = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Slack closed the Socket Mode connection.");
            JsonNode? envelope;
            try
            {
                envelope = JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (envelope is null)
                continue;
            var type = envelope["type"]?.GetValue<string>();
            if (type == "disconnect")
                throw new IOException("Slack asked to reconnect (" + (envelope["reason"]?.GetValue<string>() ?? "no reason") + ").");
            // Acknowledged before it is handled: Slack resends anything not acknowledged within three seconds.
            if (envelope["envelope_id"]?.GetValue<string>() is { } id)
                await socket.SendAsync(new JsonObject { ["envelope_id"] = id }.ToJsonString(), cancellationToken).ConfigureAwait(false);
            if (type != "events_api")
                continue;
            InboundMessage? message;
            try
            {
                message = Read(envelope["payload"]?["event"]);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                message = null;
            }
            if (message is not null)
                await inbox(message, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? last = null;
        foreach (var piece in MessageChunks.Split(message.Text, MaxMessageLength))
        {
            var body = new JsonObject { ["channel"] = message.ChatId, ["text"] = Escape(piece) };
            if (message.ThreadId is { } thread)
                body["thread_ts"] = thread;
            var sent = await CallAsync(_bot, "chat.postMessage", body, message.Tainted, cancellationToken, sending: true).ConfigureAwait(false);
            last = sent["ts"]?.GetValue<string>();
        }
        return new SentMessage(last);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { } socket)
            await socket.DisposeAsync().ConfigureAwait(false);
        if (_ownsClients)
        {
            _bot.Dispose();
            _app.Dispose();
        }
    }

    private InboundMessage? Read(JsonNode? @event)
    {
        if (@event is null || @event["bot_id"] is not null || @event["subtype"] is not null)
            return null;
        var type = @event["type"]?.GetValue<string>();
        var direct = @event["channel_type"]?.GetValue<string>() == "im";
        // Direct messages arrive as "message"; in channels only mentions are for the agent (and arrive as "app_mention").
        if (!(type == "message" && direct || type == "app_mention"))
            return null;
        if (@event["user"]?.GetValue<string>() is not { } user || user == _botUserId || @event["channel"]?.GetValue<string>() is not { } channel)
            return null;
        var text = Unescape((@event["text"]?.GetValue<string>() ?? "").Replace($"<@{_botUserId}>", "", StringComparison.Ordinal).Trim());
        return new InboundMessage(Name, channel, user, text)
        {
            MessageId = @event["ts"]?.GetValue<string>(),
            ThreadId = @event["thread_ts"]?.GetValue<string>(),
            Direct = direct,
            Addressed = true,
        };
    }

    // Slack's control sequences are <...>: <!channel>, <!here>, <@U123>. Escaping the three characters Slack asks to be escaped
    // keeps text a model wrote from pinging a channel or a person, as Discord's empty allowed_mentions does.
    internal static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Unescape(string text) =>
        text.Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    private async Task<JsonNode> CallAsync(HttpClient client, string method, JsonObject body, bool tainted,
        CancellationToken cancellationToken, bool sending = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.ApiBase, method))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        }.Tainted(tainted);
        HttpResponseMessage sent;
        try
        {
            sent = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException exception) when (sending)
        {
            throw GatewayHttp.SendRefused($"Slack {method}", exception);
        }
        using var response = sent;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            json = null;
        }
        if (response.IsSuccessStatusCode && json?["ok"]?.GetValue<bool>() == true)
            return json;
        var error = json?["error"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
        if (!sending)
            throw new HttpRequestException($"Slack {method}: {error}", null, response.StatusCode);
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        // Errors that a retry will not fix: the channel is gone, the bot is not in it, or the message itself is refused.
        var permanent = response.StatusCode != HttpStatusCode.TooManyRequests && error is "channel_not_found" or "not_in_channel"
            or "is_archived" or "msg_too_long" or "no_text" or "restricted_action" or "cannot_reply_to_message";
        throw new GatewaySendException($"Slack {method}: {error}", permanent, retryAfter);
    }
}
