using Corvid.Secrets;

namespace Corvid.Kernel.Traces;

/// <summary>
/// A screen for credentials in text that leaves the machine in a trace export (C-027). It replaces what it recognizes with
/// <c>[REDACTED]</c>, keeping a credential's public type prefix (<c>ghp_[REDACTED]</c>, <c>AKIA[REDACTED]</c>) the way secret
/// scanners report a finding, so a reader still sees that a GitHub token or an AWS key was there. The patterns are the
/// shared credential detector's (C-032, <see cref="CredentialDetector.Traces"/>), which began as Beacon's
/// (Asymptote-Labs/agent-beacon, MIT, <c>pkg/asymptoteobserve/privacy.go</c>) reduced to the common shapes.
/// </summary>
/// <remarks>
/// A screen, not a proof: a secret in no known shape and under no telling label passes. Corvid keeps real secrets behind
/// Vault placeholders, so the log should rarely hold one, but tool output (a fetched page, a command's output) can. If the
/// detector times out, the whole string is <see cref="Redacted"/>.
/// </remarks>
public static class SecretRedactor
{
    public const string Redacted = CredentialDetector.Redacted;

    /// <summary>Returns <paramref name="text"/> with every recognized credential replaced.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CredentialDetector.Redact(text, CredentialDetector.Traces);
    }
}
