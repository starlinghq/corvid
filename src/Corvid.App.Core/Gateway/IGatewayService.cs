namespace Corvid.App.Core.Gateway;

/// <summary>
/// The Gateway area's seam to the engine (docs/plan/ui.md §4.10, F16, G-14): the channel adapters and their state, pairing
/// requests approved here as guest or owner (never from a chat), paired users with their guest tool allowlists and revoke,
/// deliveries waiting, and per-chat overrides. The demo implements it now; <c>Corvid.App.Engine</c> will over the gateway
/// it hosts or the status API of the process that does.
/// </summary>
public interface IGatewayService
{
    Task<GatewaySnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Pairs the request's sender with <paramref name="role"/>; a guest starts with an empty tool allowlist.</summary>
    Task<PairedUser> PairAsync(string requestId, PairingRole role, CancellationToken cancellationToken);

    /// <summary>Drops the code: the sender stays unpaired.</summary>
    Task IgnoreAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>Unpairs a user: their next message gets a new code.</summary>
    Task RevokeAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Sets a guest's tool allowlist to <paramref name="tools"/>, each one of <see cref="GatewaySnapshot.GuestTools"/>.</summary>
    Task<PairedUser> SetAllowlistAsync(string userId, IReadOnlyList<string> tools, CancellationToken cancellationToken);

    /// <summary>An adapter, a request, a user or a delivery changed. Raised on the UI thread.</summary>
    event EventHandler? Changed;
}
