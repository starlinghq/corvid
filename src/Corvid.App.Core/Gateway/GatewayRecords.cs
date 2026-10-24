namespace Corvid.App.Core.Gateway;

/// <summary>A channel adapter's state: connected, connecting, down (it failed and retries), or off (not configured).</summary>
public enum AdapterState
{
    Up,
    Connecting,
    Down,
    Off,
    /// <summary>
    /// Enabled, but run by a gateway in another process or none: this app does not host the gateway (G-14), so it cannot
    /// see whether the adapter is connected.
    /// </summary>
    Unknown,
}

/// <summary>A channel the gateway serves (Telegram, Discord, Slack): its state, since when, the bot's account and what to know.</summary>
public sealed record GatewayAdapter(string Name, AdapterState State)
{
    public DateTimeOffset? Since { get; init; }

    /// <summary>The bot's own account on the channel ("@corvid_home_bot").</summary>
    public string Account { get; init; } = "";

    /// <summary>Why it is off or down ("no token: Vault holds no slack.bot-token").</summary>
    public string Detail { get; init; } = "";
}

/// <summary>What a paired user may do: an owner answers approvals and runs commands; a guest only talks, with the tools allowed to guests.</summary>
public enum PairingRole
{
    Guest,
    Owner,
}

/// <summary>
/// Someone messaged a bot and was given a code (F16). It is approved on this machine only, never from a chat. The handle
/// and chat come from the channel, so they are data.
/// </summary>
public sealed record PairingRequest(string Id, string Adapter, string Handle, string Code, DateTimeOffset At)
{
    /// <summary>The chat the message came from ("direct message").</summary>
    public string Chat { get; init; } = "";
}

/// <summary>A paired user, with the guest tool allowlist (empty by default) that an owner widens.</summary>
public sealed record PairedUser(string Id, string Adapter, string Handle, PairingRole Role, DateTimeOffset Since)
{
    public IReadOnlyList<string> Allowlist { get; init; } = [];
}

/// <summary>A result waiting to be delivered (a job's, a session's reply), and why it waits.</summary>
public sealed record PendingDelivery(string Id, string What, string To, DateTimeOffset Since, string Why);

/// <summary>A per-chat override: the persona and the generator slot that chat gets.</summary>
public sealed record ChatOverride(string Adapter, string Chat, string Persona, string Slot);

/// <summary>Where the gateway runs (U-8): in this app, in another process, or nowhere.</summary>
public sealed record GatewayHost(bool HostedHere, string Holder, DateTimeOffset? Since);

public sealed record GatewaySnapshot(
    GatewayHost Host,
    IReadOnlyList<GatewayAdapter> Adapters,
    IReadOnlyList<PairingRequest> Requests,
    IReadOnlyList<PairedUser> Users,
    IReadOnlyList<PendingDelivery> Deliveries,
    IReadOnlyList<ChatOverride> Overrides,
    IReadOnlyList<string> GuestTools);
