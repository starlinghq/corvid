using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Vault.Client;

namespace Corvid.Gateway.Telegram;

public sealed record TelegramOptions(SecretRef Secret)
{
    public string Name { get; init; } = "telegram";

    public Uri ApiBase { get; init; } = new("https://api.telegram.org/");

    /// <summary>Long-polling wait per <c>getUpdates</c>; Vault's own upstream timeout (100 s) is well above it.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(25);
}

/// <summary>
/// Telegram's Bot API over plain HTTP: <c>getMe</c> to connect, <c>getUpdates</c> long polling to receive, <c>sendMessage</c>
/// to send. The token is a Vault secret: the URL holds a placeholder in its place (<c>/bot{placeholder}/getUpdates</c>),
/// which Vault substitutes only for a secret whose policy allows it in a URL (<c>allowInUrl</c>), bound to
/// <c>api.telegram.org</c>. Telegram keeps unconfirmed updates, so a message is confirmed (by the next poll's offset) only
/// after the gateway has taken it: a restart receives it again rather than losing it.
/// </summary>
public sealed class TelegramAdapter : IGatewayAdapter
{
    public const int MaxMessageLength = 4096;

    private readonly TelegramOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private long _botId;
    private string _username = "";
    private long _offset;

    /// <param name="http">A client whose requests reach Telegram through Vault (<see cref="Create"/>), or a test's.</param>
    public TelegramAdapter(TelegramOptions options, HttpClient http, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        _options = options;
        _http = http;
        _ownsClient = ownsClient;
    }

    public static TelegramAdapter Create(TelegramOptions options, GatewayVault vault)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TelegramAdapter(options, GatewayHttp.CreateForUrl(vault, options.Secret, options.PollTimeout + TimeSpan.FromSeconds(30)),
            ownsClient: true);
    }

    public string Name => _options.Name;

    public async Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        var me = await CallAsync(HttpMethod.Get, "getMe", null, cancellationToken).ConfigureAwait(false);
        _botId = me["id"]?.GetValue<long>() ?? throw new InvalidDataException("getMe returned no bot id.");
        _username = me["username"]?.GetValue<string>() ?? "";
    }

    public async Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = string.Create(CultureInfo.InvariantCulture,
                $"getUpdates?offset={_offset}&timeout={(int)_options.PollTimeout.TotalSeconds}&allowed_updates=%5B%22message%22%5D");
            var updates = await CallAsync(HttpMethod.Get, query, null, cancellationToken).ConfigureAwait(false);
            foreach (var update in updates.AsArray())
            {
                if (update?["update_id"]?.GetValue<long>() is not { } id)
                    continue;
                InboundMessage? message;
                try
                {
                    message = Read(update["message"]);
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException)
                {
                    // An update of a shape this adapter does not read is skipped, not retried forever.
                    message = null;
                }
                if (message is not null)
                    await inbox(message, cancellationToken).ConfigureAwait(false);
                // Confirmed on the next poll, after the gateway has taken it.
                _offset = id + 1;
            }
        }
    }

    public async Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? last = null;
        foreach (var piece in MessageChunks.Split(message.Text, MaxMessageLength))
        {
            var body = new JsonObject
            {
                ["chat_id"] = long.TryParse(message.ChatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chat)
                    ? JsonValue.Create(chat) : JsonValue.Create(message.ChatId),
                ["text"] = piece,
            };
            if (message.ReplyTo is { } replyTo && long.TryParse(replyTo, CultureInfo.InvariantCulture, out var replyId))
                body["reply_parameters"] = new JsonObject { ["message_id"] = replyId, ["allow_sending_without_reply"] = true };
            // A thread id equal to the message replied to is "reply in a thread" outside a forum topic: Telegram shows a reply.
            // Any other thread id is a forum topic.
            if (message.ThreadId is { } thread && thread != message.ReplyTo && long.TryParse(thread, CultureInfo.InvariantCulture, out var topic))
                body["message_thread_id"] = topic;
            var sent = await CallAsync(HttpMethod.Post, "sendMessage", body, cancellationToken, message.Tainted, sending: true).ConfigureAwait(false);
            last = sent["message_id"]?.GetValue<long>().ToString(CultureInfo.InvariantCulture);
        }
        return new SentMessage(last);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private InboundMessage? Read(JsonNode? message)
    {
        if (message?["text"]?.GetValue<string>() is not { } text || message["from"] is not { } from || message["chat"] is not { } chat)
            return null;
        if (from["is_bot"]?.GetValue<bool>() == true)
            return null;
        var direct = chat["type"]?.GetValue<string>() == "private";
        var mention = _username.Length > 0 ? "@" + _username : null;
        var repliesToBot = message["reply_to_message"]?["from"]?["id"]?.GetValue<long>() == _botId;
        var command = text.StartsWith('/');
        // "/status@OtherBot" is for another bot in the group.
        if (command && text.Split(' ', 2)[0] is var word && word.Contains('@', StringComparison.Ordinal)
            && (mention is null || !word.EndsWith(mention, StringComparison.OrdinalIgnoreCase)))
            return null;
        var addressed = direct || repliesToBot || command || mention is not null && text.Contains(mention, StringComparison.OrdinalIgnoreCase);
        if (mention is not null)
            text = text.Replace(mention, "", StringComparison.OrdinalIgnoreCase).Trim();
        var topic = message["is_topic_message"]?.GetValue<bool>() == true ? message["message_thread_id"]?.GetValue<long>() : null;
        return new InboundMessage(Name, Id(chat["id"]), Id(from["id"]), text)
        {
            SenderName = from["username"]?.GetValue<string>() ?? from["first_name"]?.GetValue<string>(),
            MessageId = Id(message["message_id"]),
            ThreadId = topic?.ToString(CultureInfo.InvariantCulture),
            Direct = direct,
            Addressed = addressed,
        };
    }

    private static string Id(JsonNode? node) => node?.GetValue<long>().ToString(CultureInfo.InvariantCulture) ?? "";

    private async Task<JsonNode> CallAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken,
        bool tainted = false, bool sending = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.ApiBase, "bot" + VaultUrlSecretHandler.Marker + "/" + path)).Tainted(tainted);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage sent;
        try
        {
            sent = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException exception) when (sending)
        {
            throw GatewayHttp.SendRefused("Telegram sendMessage", exception);
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
        if (json?["ok"]?.GetValue<bool>() == true && json["result"] is { } result)
            return result;
        var description = json?["description"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
        if (!sending)
            throw new HttpRequestException($"Telegram {path.Split('?')[0]}: {description}", null, response.StatusCode);
        var status = json?["error_code"]?.GetValue<int>() ?? (int)response.StatusCode;
        TimeSpan? retryAfter = json?["parameters"]?["retry_after"]?.GetValue<int>() is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        // 400 (chat not found, message too long) and 403 (the bot was blocked or removed) will not change on a retry.
        var permanent = status is (int)HttpStatusCode.BadRequest or (int)HttpStatusCode.Forbidden or (int)HttpStatusCode.NotFound;
        throw new GatewaySendException($"Telegram sendMessage: {description}", permanent, retryAfter);
    }
}
