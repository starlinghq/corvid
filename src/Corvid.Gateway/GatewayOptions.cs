using System.Collections.Immutable;
using Corvid.Approvals;

namespace Corvid.Gateway;

/// <summary>Per-channel settings (plan §12): a persona added to the system prompt and slot overrides, by catalogue model name.</summary>
public sealed record ChatSettings(string? Persona = null, IReadOnlyDictionary<string, string>? Slots = null)
{
    public static ChatSettings None { get; } = new();

    public IReadOnlyDictionary<string, string> Slots { get; } = Slots ?? ImmutableDictionary<string, string>.Empty;
}

/// <summary>One adapter's people and channels.</summary>
public sealed record AdapterOptions(string Name)
{
    /// <summary>Platform user ids that are the owner without pairing (an allowlist). Their messages are the user's.</summary>
    public ImmutableHashSet<string> Owners { get; init; } = [];

    /// <summary>Platform user ids allowed to talk to the agent without pairing. Their messages are tainted (plan §10.3).</summary>
    public ImmutableHashSet<string> Guests { get; init; } = [];

    /// <summary>Where <c>deliver: home_channel</c> sends; without one, such answers are replies.</summary>
    public string? HomeChat { get; init; }

    /// <summary>Overrides by chat id.</summary>
    public ImmutableDictionary<string, ChatSettings> Chats { get; init; } = ImmutableDictionary<string, ChatSettings>.Empty;

    public ChatSettings SettingsFor(string chatId) => Chats.TryGetValue(chatId, out var settings) ? settings : ChatSettings.None;
}

public sealed record GatewayOptions
{
    public static GatewayOptions Default { get; } = new();

    public ImmutableDictionary<string, AdapterOptions> Adapters { get; init; } = ImmutableDictionary<string, AdapterOptions>.Empty;

    /// <summary>A connect that takes longer is abandoned, and the adapter retried after a backoff (#67498, #72589 in Hermes).</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long stopping waits for one adapter, and for replies still being written, before leaving them.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The first wait before reconnecting a failed adapter; it doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the ledger worker looks for deliveries other processes queued (a CLI that ran a job).</summary>
    public TimeSpan LedgerPoll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Send attempts before a delivery is given up as failed.</summary>
    public int MaxDeliveryAttempts { get; init; } = 8;

    /// <summary>The first wait before resending; it doubles up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long an approval asked in a chat waits for the owner (the pipeline's own deadline still applies).</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public AdapterOptions For(string adapter) => Adapters.TryGetValue(adapter, out var options) ? options : new AdapterOptions(adapter);
}

/// <summary>A turn's answer, for the gateway to route.</summary>
/// <param name="Text">The answer; null when the turn was cancelled and there is nothing to send.</param>
/// <param name="Deliver">The <c>deliver</c> option the turn chose (<c>reply</c> unless a calibrated answer said otherwise).</param>
/// <param name="Tainted">The session held outside content when it answered.</param>
/// <param name="Sequence">The session's head after the answer, for the ledger's source.</param>
/// <param name="Failed">The turn threw; <paramref name="Text"/> is a short notice, not an answer.</param>
public sealed record AgentReply(string? Text, string Deliver, bool Tainted, long Sequence, bool Failed = false);

/// <summary>
/// What the gateway needs from the agent, implemented by the host over its runtime, so the gateway reaches sessions, slash
/// commands and approvals without referencing the composition root.
/// </summary>
public interface IGatewayAgent
{
    /// <summary>Prepares a chat's session before its first message: surface, persona, slot overrides, the deliver question.</summary>
    Task OpenAsync(string sessionId, string adapter, ChatSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Posts a message to the session. The outer task completes once it is queued (so callers keep a chat's order without
    /// waiting for turns); the inner one with the answer. <paramref name="fromOwner"/> false makes it outside content.
    /// </summary>
    ValueTask<Task<AgentReply>> PostAsync(string sessionId, string text, bool fromOwner, CancellationToken cancellationToken);

    /// <summary>Whether a line is a slash command of the shared registry (a pasted path is not).</summary>
    bool IsCommand(string text);

    /// <summary>Runs a command from the shared registry in the session and returns its output as plain text.</summary>
    Task<string> RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken);

    /// <summary>Routes the session's approval prompts to <paramref name="prompter"/> until disposed.</summary>
    IDisposable AttachPrompter(string sessionId, IApprovalPrompter prompter);
}

/// <summary>Gateway session ids: one session per chat, <c>gateway:{adapter}:{chat}</c>, so a job's origin session names its chat.</summary>
public static class GatewaySessions
{
    public const string Prefix = "gateway:";

    /// <summary>The ledger's own session in the event log.</summary>
    public const string Ledger = "gateway-ledger";

    public static string For(string adapter, string chatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        if (adapter.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("An adapter name cannot contain ':'.", nameof(adapter));
        return Prefix + adapter + ":" + chatId;
    }

    /// <summary>The adapter and chat of a gateway session id; false for any other session (the CLI's, a job run's).</summary>
    public static bool TryParse(string? sessionId, out string adapter, out string chatId)
    {
        adapter = chatId = "";
        if (sessionId is null || !sessionId.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var rest = sessionId[Prefix.Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == rest.Length - 1)
            return false;
        (adapter, chatId) = (rest[..colon], rest[(colon + 1)..]);
        return true;
    }
}
