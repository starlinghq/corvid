namespace Corvid.Gateway;

/// <summary>A message a platform delivered to the bot.</summary>
/// <param name="Adapter">The adapter's name: <c>telegram</c>, <c>discord</c>, <c>slack</c>.</param>
/// <param name="ChatId">The platform's conversation: a Telegram chat, a Discord channel, a Slack channel or DM.</param>
/// <param name="SenderId">The platform's id of the person who wrote it; pairing and taint go by it.</param>
/// <param name="Text">The text, with the bot's own mention removed.</param>
public sealed record InboundMessage(string Adapter, string ChatId, string SenderId, string Text)
{
    public string? SenderName { get; init; }

    /// <summary>The platform's id for this message, for replies and threads.</summary>
    public string? MessageId { get; init; }

    /// <summary>The thread the message was written in, if any (a Slack <c>thread_ts</c>, a Telegram topic).</summary>
    public string? ThreadId { get; init; }

    /// <summary>A one-to-one conversation with the bot, rather than a group or channel.</summary>
    public bool Direct { get; init; }

    /// <summary>
    /// In a group: the message names the bot (a mention, a reply to it, a command). Unaddressed group messages are not for
    /// the agent and are dropped; direct messages are always addressed.
    /// </summary>
    public bool Addressed { get; init; }
}

/// <summary>What the gateway asks an adapter to send.</summary>
/// <param name="ReplyTo">The platform message this answers, when the platform can show that.</param>
/// <param name="ThreadId">The thread to post in; for <c>reply_in_thread</c>, the message that starts it.</param>
/// <param name="Tainted">The text came from a turn holding outside content (plan §10.3); the request tells Vault so.</param>
public sealed record OutboundMessage(string ChatId, string Text, string? ReplyTo = null, string? ThreadId = null, bool Tainted = false);

/// <param name="MessageId">The platform's id of the (last) message sent, when it returns one.</param>
public sealed record SentMessage(string? MessageId);

/// <summary>
/// A send the platform refused. <see cref="Permanent"/> failures (an unknown chat, a bot removed from a channel, a message
/// the platform will never accept) are given up at once; others are retried, after <see cref="RetryAfter"/> when the platform
/// says how long to wait.
/// </summary>
public sealed class GatewaySendException : Exception
{
    public GatewaySendException(string message, bool permanent = false, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Permanent = permanent;
        RetryAfter = retryAfter;
    }

    public GatewaySendException()
        : this("The platform refused the message.")
    {
    }

    public GatewaySendException(string message)
        : this(message, false)
    {
    }

    public GatewaySendException(string message, Exception innerException)
        : this(message, false, null, innerException)
    {
    }

    public bool Permanent { get; }

    public TimeSpan? RetryAfter { get; }
}

/// <summary>What the gateway gives an adapter: who is known, and a small durable store for its read position.</summary>
public interface IAdapterContext
{
    /// <summary>Owners and guests of this adapter, from configuration and pairing (Discord opens their DM channels).</summary>
    IReadOnlyList<string> KnownUsers();

    /// <summary>A value the adapter saved earlier (a polling cursor), or null.</summary>
    string? GetCursor(string key);

    void SetCursor(string key, string value);
}

/// <summary>
/// One messaging platform (plan §12). The gateway creates an adapter only when it is enabled, each in its own scope (its own
/// instance, cancellation and HTTP client), connects it under a timeout, and receives on it until it fails or the gateway
/// stops; a failed adapter is disposed and a new one created after a backoff, so one platform never stalls another.
/// </summary>
public interface IGatewayAdapter : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Authenticates and gets ready to receive. Returns once connected; the gateway gives up on it after its timeout.</summary>
    Task ConnectAsync(IAdapterContext context, CancellationToken cancellationToken);

    /// <summary>Receives until cancelled, handing each message to <paramref name="inbox"/> in order. Throws when the connection fails.</summary>
    Task ReceiveAsync(Func<InboundMessage, CancellationToken, Task> inbox, CancellationToken cancellationToken);

    /// <summary>Sends one message, split into the platform's maximum length when needed.</summary>
    /// <exception cref="GatewaySendException">The platform refused it.</exception>
    Task<SentMessage> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>An enabled adapter: its name and how to create a fresh instance. Nothing is created until the gateway starts it.</summary>
public sealed record GatewayAdapterRegistration(string Name, Func<IGatewayAdapter> Create);

/// <summary>Splits text into pieces a platform accepts, preferring line breaks, then spaces, and never a surrogate pair.</summary>
public static class MessageChunks
{
    public static IReadOnlyList<string> Split(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 16);
        if (text.Length <= max)
            return [text];
        var pieces = new List<string>();
        var rest = text.AsSpan();
        while (rest.Length > max)
        {
            var window = rest[..max];
            var cut = window.LastIndexOf('\n');
            if (cut < max / 2)
                cut = window.LastIndexOf(' ');
            if (cut < max / 2)
                cut = char.IsHighSurrogate(window[^1]) ? max - 1 : max;
            pieces.Add(rest[..cut].ToString());
            rest = rest[cut..].TrimStart('\n');
        }
        if (rest.Length > 0)
            pieces.Add(rest.ToString());
        return pieces;
    }
}
