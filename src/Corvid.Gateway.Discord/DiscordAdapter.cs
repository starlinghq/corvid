using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Vault.Client;

namespace Corvid.Gateway.Discord;

/// <param name="Channels">Guild channels to watch, by id; the bot answers there when mentioned.</param>
public sealed record DiscordOptions(SecretRef Secret)
{
    public string Name { get; init; } = "discord";

    public Uri ApiBase { get; init; } = new("https://discord.com/api/v10/");

    public ImmutableArray<string> Channels { get; init; } = [];

    /// <summary>How often each channel is polled. Discord allows about 50 requests a second per bot; a few channels at 5 s is far below.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Discord over its REST API, with the bot token a Vault secret sent as an <c>Authorization: Bot</c> placeholder through
/// Vault's proxy. Discord's realtime Gateway WebSocket needs the token inside its first frame (<c>IDENTIFY</c>), which Vault
/// cannot substitute yet (vault.md §9, not built), so this adapter polls instead: the configured guild channels, and a DM
/// channel opened with each known user (owners and guests from configuration and pairing). It therefore cannot see a DM from
/// someone it does not know yet; a new person asks to pair by mentioning the bot in a watched channel. Each channel's read
/// position is kept in the gateway's store, so messages written while the gateway was down are read after it starts.
/// Answers never ping anyone: <c>allowed_mentions</c> is empty, so text a model wrote cannot reach <c>@everyone</c>.
/// </summary>
public sealed class DiscordAdapter : IGatewayAdapter
{
    public const int MaxMessageLength = 2000;

    // Discord's JSON error code for "a thread has already been created for this message".
    private const int ThreadAlreadyCreated = 160004;
    private const string ErrorCodeKey = "discord.code";

    private readonly DiscordOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Dictionary<string, string> _dms = new(StringComparer.Ordinal); // channel → user
    private readonly HashSet<string> _dmUsers = new(StringComparer.Ordinal);
    private IAdapterContext? _context;
    private string _botId = "";

    public DiscordAdapter(DiscordOptions options, HttpClient http, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http);
        _options = options;
        _http = http;
        _ownsClient = ownsClient;
    }

    public static DiscordAdapter Create(DiscordOptions options, GatewayVault vault)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DiscordAdapter(options, GatewayHttp.Create(vault, new SecretHeader(options.Secret, "Authorization", "Bot"), TimeSpan.FromSeconds(30)),
            ownsClient: true);
    }

    public string Name => _options.Name;

    public async Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        var me = await CallAsync(HttpMethod.Get, "users/@me", null, false, cancellationToken).ConfigureAwait(false);
        _botId = me["id"]?.GetValue<string>() ?? throw new InvalidDataException("users/@me returned no id.");
        await OpenDirectChannelsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var channel in Watched())
            await StartCursorAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        var context = _context ?? throw new InvalidOperationException("Connect first.");
        while (true)
        {
            // People paired since the last round get their DM channel watched.
            await OpenDirectChannelsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var channel in Watched())
                await PollAsync(context, channel, inbox, cancellationToken).ConfigureAwait(false);
            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var channel = message.ChatId;
        var replyTo = message.ReplyTo;
        // reply_in_thread: start a thread on the message and answer inside it (a thread is a channel of its own).
        if (message.ThreadId is { } thread && thread == message.ReplyTo)
        {
            try
            {
                var created = await CallAsync(HttpMethod.Post, $"channels/{channel}/messages/{thread}/threads",
                    new JsonObject { ["name"] = "Corvid", ["auto_archive_duration"] = 1440 }, message.Tainted, cancellationToken, sending: true)
                    .ConfigureAwait(false);
                channel = created["id"]?.GetValue<string>() ?? channel;
                replyTo = null;
            }
            catch (GatewaySendException exception) when (exception.Data[ErrorCodeKey] is ThreadAlreadyCreated)
            {
                // A resend after the thread was made: a thread started on a message has that message's id.
                channel = thread;
                replyTo = null;
            }
            catch (GatewaySendException exception) when (exception.Permanent)
            {
                // No thread here (a DM, or no permission to create threads): an ordinary reply rather than nothing.
            }
        }
        string? last = null;
        foreach (var piece in MessageChunks.Split(message.Text, MaxMessageLength))
        {
            var body = new JsonObject
            {
                ["content"] = piece,
                ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
            };
            if (replyTo is not null)
                body["message_reference"] = new JsonObject { ["message_id"] = replyTo, ["fail_if_not_exists"] = false };
            var sent = await CallAsync(HttpMethod.Post, $"channels/{channel}/messages", body, message.Tainted, cancellationToken, sending: true)
                .ConfigureAwait(false);
            last = sent["id"]?.GetValue<string>();
        }
        return new SentMessage(last);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private IEnumerable<string> Watched() => _options.Channels.Concat(_dms.Keys).Distinct(StringComparer.Ordinal).ToList();

    private async Task OpenDirectChannelsAsync(CancellationToken cancellationToken)
    {
        foreach (var user in _context!.KnownUsers().Where(user => !_dmUsers.Contains(user)))
        {
            var dm = await CallAsync(HttpMethod.Post, "users/@me/channels", new JsonObject { ["recipient_id"] = user }, false, cancellationToken)
                .ConfigureAwait(false);
            if (dm["id"]?.GetValue<string>() is not { } channel)
                continue;
            _dms[channel] = user;
            _dmUsers.Add(user);
            await StartCursorAsync(channel, cancellationToken).ConfigureAwait(false);
        }
    }

    // A channel seen for the first time starts at its newest message: history from before the bot watched it is not replayed.
    private async Task StartCursorAsync(string channel, CancellationToken cancellationToken)
    {
        if (_context!.GetCursor(channel) is not null)
            return;
        var latest = await CallAsync(HttpMethod.Get, $"channels/{channel}/messages?limit=1", null, false, cancellationToken).ConfigureAwait(false);
        _context.SetCursor(channel, latest.AsArray().FirstOrDefault()?["id"]?.GetValue<string>() ?? "0");
    }

    private async Task PollAsync(IAdapterContext context, string channel, Func<InboundMessage, CancellationToken, Task> inbox,
        CancellationToken cancellationToken)
    {
        var after = context.GetCursor(channel) ?? "0";
        JsonNode page;
        try
        {
            page = await CallAsync(HttpMethod.Get, $"channels/{channel}/messages?after={after}&limit=50", null, false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // A channel the bot cannot read (removed, or no access): the others are still polled.
            return;
        }
        catch (RateLimitedException limited)
        {
            // Polling waits out the rate limit rather than tearing down the connection.
            await Task.Delay(limited.RetryAfter, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Newest first from Discord; snowflakes order by time.
        foreach (var item in page.AsArray().OrderBy(item => Snowflake(item?["id"])))
        {
            if (item?["id"]?.GetValue<string>() is not { } id)
                continue;
            InboundMessage? message;
            try
            {
                message = Read(item, channel);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                message = null;
            }
            if (message is not null)
                await inbox(message, cancellationToken).ConfigureAwait(false);
            context.SetCursor(channel, id);
        }
    }

    private InboundMessage? Read(JsonNode item, string channel)
    {
        var author = item["author"];
        if (author is null || author["bot"]?.GetValue<bool>() == true || author["id"]?.GetValue<string>() is not { } user || user == _botId)
            return null;
        var direct = _dms.ContainsKey(channel);
        var mentioned = item["mentions"]?.AsArray().Any(mention => mention?["id"]?.GetValue<string>() == _botId) == true;
        var text = (item["content"]?.GetValue<string>() ?? "")
            .Replace($"<@{_botId}>", "", StringComparison.Ordinal).Replace($"<@!{_botId}>", "", StringComparison.Ordinal).Trim();
        return new InboundMessage(Name, channel, user, text)
        {
            SenderName = author["global_name"]?.GetValue<string>() ?? author["username"]?.GetValue<string>(),
            MessageId = item["id"]?.GetValue<string>(),
            Direct = direct,
            Addressed = direct || mentioned,
        };
    }

    private static ulong Snowflake(JsonNode? id) =>
        ulong.TryParse(id?.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private async Task<JsonNode> CallAsync(HttpMethod method, string path, JsonObject? body, bool tainted, CancellationToken cancellationToken,
        bool sending = false)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.ApiBase, path)).Tainted(tainted);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage sent;
        try
        {
            sent = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultException exception) when (sending)
        {
            throw GatewayHttp.SendRefused($"Discord {path.Split('?')[0]}", exception);
        }
        using var response = sent;
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? json;
        try
        {
            json = text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            json = null;
        }
        if (response.IsSuccessStatusCode && json is not null)
            return json;
        var detail = json?["message"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
        TimeSpan? retryAfter = json?["retry_after"]?.GetValue<double>() is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests && !sending)
            throw new RateLimitedException(retryAfter ?? TimeSpan.FromSeconds(1));
        if (!sending)
            throw new HttpRequestException($"Discord {method} {path.Split('?')[0]}: {detail}", null, response.StatusCode);
        var permanent = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;
        var refused = new GatewaySendException($"Discord {path.Split('?')[0]}: {detail}", permanent, retryAfter);
        if (json?["code"] is JsonValue code && code.TryGetValue<int>(out var number))
            refused.Data[ErrorCodeKey] = number;
        throw refused;
    }

    private sealed class RateLimitedException(TimeSpan retryAfter) : Exception("Discord rate limit")
    {
        public TimeSpan RetryAfter { get; } = retryAfter;
    }
}
