using System.Text.Json.Serialization;

namespace Corvid.Vault.Contracts;

/// <summary>A request from Corvid to Vault. Every request names the Corvid session it belongs to.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(CatalogRequest), "catalog")]
[JsonDerivedType(typeof(PlaceholderRequest), "placeholder")]
[JsonDerivedType(typeof(ForwardRequest), "forward")]
[JsonDerivedType(typeof(ScrubRequest), "scrub")]
[JsonDerivedType(typeof(AuditSummaryRequest), "audit_summary")]
[JsonDerivedType(typeof(SandboxOpenRequest), "sandbox_open")]
[JsonDerivedType(typeof(SandboxCloseRequest), "sandbox_close")]
public abstract class VaultRequest
{
    /// <summary>Corvid session id. Placeholders are bound to the session that minted them.</summary>
    public required string SessionId { get; init; }
}

/// <summary>List secret names, descriptions and bindings. Never values.</summary>
public sealed class CatalogRequest : VaultRequest;

/// <summary>Mint a placeholder for a secret, valid for this session and this peer only.</summary>
public sealed class PlaceholderRequest : VaultRequest
{
    public required string Secret { get; init; }
}

/// <summary>
/// An HTTP request whose headers carry placeholders. Vault checks policy, substitutes the values, sends the request
/// itself and returns the response scrubbed.
/// </summary>
public sealed class ForwardRequest : VaultRequest
{
    public required string Method { get; init; }

    public required string Url { get; init; }

    /// <summary>Request and content headers, in order. A header may repeat.</summary>
    public IReadOnlyList<WireHeader> Headers { get; init; } = [];

    public byte[]? Body { get; init; }

    /// <summary>The user approved this request in Corvid's own approval flow (satisfies an <c>ask</c> rule, never <c>confirm</c>).</summary>
    public bool Approved { get; init; }

    /// <summary>The turn holds untrusted content. Vault only ever becomes stricter because of this flag.</summary>
    public bool Tainted { get; init; }

    /// <summary>The toolset the calling tool belongs to, checked against the secret's allowed toolsets.</summary>
    public string? Toolset { get; init; }
}

/// <summary>Replace any secret value in the text, in any supported encoding, with <c>[secret:name]</c>.</summary>
public sealed class ScrubRequest : VaultRequest
{
    public required string Text { get; init; }
}

/// <summary>Counts of audited events by decision, for Corvid's insights view.</summary>
public sealed class AuditSummaryRequest : VaultRequest;

/// <summary>
/// Open a sandbox proxy (V-003a): a Unix socket of its own, served by Vault as an HTTP CONNECT proxy that terminates TLS
/// with a per-sandbox CA and substitutes this session's placeholders for bound hosts. The sandbox acts as the opener's
/// session and uid; Vault never becomes less strict because of these flags.
/// </summary>
public sealed class SandboxOpenRequest : VaultRequest
{
    /// <summary>The toolset the sandboxed command runs under, checked against each secret's allowed toolsets.</summary>
    public string? Toolset { get; init; }

    /// <summary>The turn holds untrusted content; raises each secret's rule to its <c>tainted</c> floor.</summary>
    public bool Tainted { get; init; }

    /// <summary>
    /// The command's toolset grants network access. Only then may requests that carry no placeholder pass through (still
    /// HTTPS-only, upstream-verified and scrubbed). Without it, only hosts some secret is bound to are reachable.
    /// </summary>
    public bool Network { get; init; }

    /// <summary>Requested lifetime in minutes; Vault caps it at its own <c>sandboxLifetimeMinutes</c>.</summary>
    public int? LifetimeMinutes { get; init; }
}

/// <summary>Close a sandbox proxy: its socket, every live connection and its CA go away.</summary>
public sealed class SandboxCloseRequest : VaultRequest
{
    public required string SandboxId { get; init; }
}

public sealed record WireHeader(string Name, string Value);

/// <summary>A response from Vault.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CatalogResponse), "catalog")]
[JsonDerivedType(typeof(PlaceholderResponse), "placeholder")]
[JsonDerivedType(typeof(ForwardResponse), "forward")]
[JsonDerivedType(typeof(ScrubResponse), "scrub")]
[JsonDerivedType(typeof(AuditSummaryResponse), "audit_summary")]
[JsonDerivedType(typeof(SandboxOpenResponse), "sandbox_open")]
[JsonDerivedType(typeof(SandboxClosedResponse), "sandbox_closed")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
public abstract class VaultResponse;

public sealed class CatalogResponse : VaultResponse
{
    public IReadOnlyList<CatalogEntry> Secrets { get; init; } = [];
}

/// <param name="Name">Secret name, e.g. <c>github.token</c>.</param>
/// <param name="Description">What the secret is for, as the user described it.</param>
/// <param name="Hosts">Hosts the secret may be sent to. Empty means it is stored but unusable until policy binds it.</param>
/// <param name="Toolsets">Toolsets allowed to reference it; empty means any.</param>
public sealed record CatalogEntry(string Name, string Description, IReadOnlyList<string> Hosts, IReadOnlyList<string> Toolsets);

public sealed class PlaceholderResponse : VaultResponse
{
    public required string Placeholder { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class ForwardResponse : VaultResponse
{
    public required int Status { get; init; }

    public string? Reason { get; init; }

    /// <summary>Response and content headers, scrubbed.</summary>
    public IReadOnlyList<WireHeader> Headers { get; init; } = [];

    /// <summary>The response body, scrubbed.</summary>
    public byte[] Body { get; init; } = [];

    /// <summary>Number of secret values replaced in headers and body.</summary>
    public int ScrubHits { get; init; }
}

public sealed class ScrubResponse : VaultResponse
{
    public required string Text { get; init; }

    public int Hits { get; init; }
}

public sealed class AuditSummaryResponse : VaultResponse
{
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
}

public sealed class SandboxOpenResponse : VaultResponse
{
    /// <summary>Twelve lower-case alphanumerics; names the sandbox in <see cref="SandboxCloseRequest"/> and the audit log.</summary>
    public required string SandboxId { get; init; }

    /// <summary>The Unix socket (mode 0600, in a 0700 directory) the sandbox's HTTPS proxy client connects to.</summary>
    public required string ProxySocketPath { get; init; }

    /// <summary>The sandbox's CA certificate, PEM. Public part only; the key never leaves Vault's memory.</summary>
    public required string CaCertificatePem { get; init; }

    /// <summary>When the socket and CA die if the sandbox is not closed first.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed class SandboxClosedResponse : VaultResponse
{
    public required string SandboxId { get; init; }
}

public sealed class ErrorResponse : VaultResponse
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}

/// <summary>Error codes in <see cref="ErrorResponse.Code"/>.</summary>
public static class VaultErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string UnknownSecret = "unknown_secret";
    public const string BadPlaceholder = "bad_placeholder";
    public const string PlaceholderOutsideHeaders = "placeholder_outside_headers";
    public const string PlaceholderRepeated = "placeholder_repeated";
    public const string HostNotBound = "host_not_bound";
    public const string InsecureTransport = "insecure_transport";
    public const string Denied = "denied";
    public const string ApprovalRequired = "approval_required";
    public const string ConfirmUnavailable = "confirm_unavailable";
    public const string ToolsetNotAllowed = "toolset_not_allowed";
    public const string BudgetExceeded = "budget_exceeded";
    public const string RateLimited = "rate_limited";
    public const string UpstreamError = "upstream_error";
    public const string ResponseTooLarge = "response_too_large";
    public const string UnknownSandbox = "unknown_sandbox";
    public const string NetworkNotGranted = "network_not_granted";
    public const string SandboxLimit = "sandbox_limit";
    public const string NotProxyable = "not_proxyable";
}
