using System.Globalization;
using Corvid.Approvals;
using Corvid.Vault.Client;

namespace Corvid.Tools.Builtin;

/// <summary>
/// Opens Corvid Vault's per-sandbox proxy for one shell command (V-003b): the socket the sandbox relays to and the CA it
/// trusts. A lease is closed when the command ends; Vault's expiry is the backstop.
/// </summary>
public interface ISandboxEgress
{
    /// <param name="tainted">The turn holds outside content; Vault raises each secret's rule to its tainted floor.</param>
    /// <param name="network">Placeholder-free requests may pass through (HTTPS, scrubbed); otherwise only bound hosts.</param>
    /// <param name="lifetime">How long Vault keeps the proxy if it is never closed.</param>
    Task<SandboxEgressLease> OpenAsync(bool tainted, bool network, TimeSpan lifetime, CancellationToken cancellationToken);
}

/// <summary>An open sandbox proxy. Disposing it closes the proxy, its live connections and its CA.</summary>
/// <param name="id">Vault's sandbox id, as its audit log names it.</param>
/// <param name="socketPath">The per-sandbox Unix socket.</param>
/// <param name="caCertificatePem">The sandbox CA, public part only.</param>
/// <param name="expiresAt">When Vault closes it if nobody does.</param>
/// <param name="close">Closes it in Vault; called once.</param>
public sealed class SandboxEgressLease(string id, string socketPath, string caCertificatePem, DateTimeOffset expiresAt, Func<Task> close)
    : IAsyncDisposable
{
    private Func<Task>? _close = close ?? throw new ArgumentNullException(nameof(close));

    public string Id { get; } = id;

    public string SocketPath { get; } = socketPath;

    public string CaCertificatePem { get; } = caCertificatePem;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    /// <summary>Closes the proxy once; later calls do nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _close, null) is { } close)
            await close().ConfigureAwait(false);
    }
}

/// <summary>
/// <see cref="ISandboxEgress"/> over Vault's <c>sandbox_open</c> and <c>sandbox_close</c>, in one Vault session per Corvid
/// process, with the toolset <c>shell</c>: a secret meant for scripts lists <c>shell</c> in its toolsets, or none.
/// </summary>
public sealed class VaultSandboxEgress : ISandboxEgress
{
    public const string Toolset = "shell";

    private readonly VaultClient _client;

    public VaultSandboxEgress(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _client = new VaultClient(
            "corvid-shell-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8], socketPath);
    }

    /// <summary>The Vault session the proxies are opened in; placeholders a script uses must be minted in it.</summary>
    public VaultClient Client => _client;

    public async Task<SandboxEgressLease> OpenAsync(bool tainted, bool network, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var minutes = (int)Math.Clamp(Math.Ceiling(lifetime.TotalMinutes), 1, int.MaxValue);
        var open = await _client.OpenSandboxAsync(Toolset, tainted, network, minutes, cancellationToken).ConfigureAwait(false);
        return new SandboxEgressLease(open.SandboxId, open.ProxySocketPath, open.CaCertificatePem, open.ExpiresAt,
            () => _client.CloseSandboxAsync(open.SandboxId, CancellationToken.None));
    }
}

/// <summary>
/// What a command's network route depends on: the session, whether its turn is tainted, and the approval that let it run.
/// </summary>
/// <param name="Approval">The approval pipeline's result for this command, when there was one.</param>
public sealed record ShellEgress(string SessionId, bool Tainted, ApprovalResult? Approval)
{
    /// <summary>
    /// In a tainted turn, a route only when a person approved this command after being told it reaches the network in a
    /// turn holding outside content (<see cref="ApprovalReasons.TaintedEgress"/>). A command the parser cannot see the
    /// network in (<c>python3 x.py</c>) is never asked about that, so it runs without a route.
    /// </summary>
    public bool MayRoute => !Tainted || Approval is { Allowed: true, Resolution: ApprovalResolution.UserApproved } approval
        && approval.Assessment.Reasons.Any(reason => reason.Code == ApprovalReasons.TaintedEgress);
}

/// <summary>A shell command's result and its network route.</summary>
/// <param name="Network"><see cref="ShellTelemetry.RouteVault"/>, <see cref="ShellTelemetry.RouteWithheld"/>, or null when the shell has no network.</param>
public sealed record ShellRun(Corvid.Sandbox.SandboxResult Result, string? Network);

/// <summary>Span and tag names for sandbox egress, on <see cref="Corvid.Kernel.KernelTelemetry.Source"/> under the tool's span.</summary>
public static class ShellTelemetry
{
    /// <summary>One command's Vault proxy, from open to close.</summary>
    public const string EgressSpan = "corvid.sandbox.egress";

    /// <summary><see cref="RouteVault"/> or <see cref="RouteWithheld"/>.</summary>
    public const string Route = "corvid.sandbox.route";

    public const string RouteVault = "vault";

    public const string RouteWithheld = "withheld";

    public const string SandboxId = "corvid.vault.sandbox_id";

    /// <summary>Whether placeholder-free requests may pass through.</summary>
    public const string Passthrough = "corvid.sandbox.passthrough";

    public const string Tainted = "corvid.sandbox.tainted";

    public const string Expires = "corvid.vault.sandbox_expires";

    /// <summary>Vault's error code (or the exception's type) when the proxy could not be opened.</summary>
    public const string Error = "error.type";

    /// <summary>Event on the span when the proxy was closed.</summary>
    public const string ClosedEvent = "sandbox_closed";
}
