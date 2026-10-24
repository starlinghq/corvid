namespace Corvid.App.Core.Mcp;

/// <summary>
/// The MCP area's seam to the engine (docs/plan/ui.md §4.10, F15), the operations <c>/mcp</c> runs: the configured servers
/// with their trust and state, the held tool definitions with their pins, accept (pins exactly the hash reviewed), allow (a
/// flagged or withheld one, as a tool that always asks), forget (a server's pins), trust, and adding a server. The demo
/// implements it now; <c>Corvid.App.Engine</c> will over <c>McpClients</c> and the pin store.
/// </summary>
public interface IMcpService
{
    Task<McpSnapshot> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pins the held definition whose hash is <paramref name="hash"/> and offers the tool from the next turn. Refused
    /// (<see cref="InvalidOperationException"/>) when the definition changed since (another hash), or when it needs allow.
    /// </summary>
    Task AcceptAsync(string server, string tool, string hash, CancellationToken cancellationToken);

    /// <summary>Offers a flagged or withheld definition as an untrusted server's tool: it asks before every call.</summary>
    Task AllowAsync(string server, string tool, string hash, CancellationToken cancellationToken);

    /// <summary>Keeps a held tool blocked: it stays held and is not offered; the decision is the person's, recorded.</summary>
    Task KeepBlockedAsync(string server, string tool, CancellationToken cancellationToken);

    /// <summary>Drops a server's pins; returns how many. Its next start pins whatever it lists (trust on first use).</summary>
    Task<int> ForgetAsync(string server, CancellationToken cancellationToken);

    Task SetTrustAsync(string server, McpTrust trust, CancellationToken cancellationToken);

    /// <summary>Checks and adds a server to the configuration; it starts with the next session.</summary>
    Task<McpServerRecord> AddServerAsync(McpServerDraft draft, CancellationToken cancellationToken);

    /// <summary>A server, a tool or a pin changed. Raised on the UI thread.</summary>
    event EventHandler? Changed;
}
