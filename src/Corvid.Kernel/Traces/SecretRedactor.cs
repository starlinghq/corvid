using System.Text.RegularExpressions;

namespace Corvid.Kernel.Traces;

/// <summary>
/// A screen for credentials in text that leaves the machine in a trace export (C-027). It replaces what it recognizes with
/// <c>[REDACTED]</c>, keeping a credential's public type prefix (<c>ghp_[REDACTED]</c>, <c>AKIA[REDACTED]</c>) the way secret
/// scanners report a finding, so a reader still sees that a GitHub token or an AWS key was there. The patterns follow
/// Beacon's (Asymptote-Labs/agent-beacon, MIT, <c>pkg/asymptoteobserve/privacy.go</c>), reduced to the common shapes.
/// </summary>
/// <remarks>
/// A screen, not a proof: a secret in no known shape and under no telling label passes. Corvid keeps real secrets behind
/// Vault placeholders, so the log should rarely hold one, but tool output (a fetched page, a command's output) can.
/// </remarks>
public static partial class SecretRedactor
{
    public const string Redacted = "[REDACTED]";

    /// <summary>Returns <paramref name="text"/> with every recognized credential replaced.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            return text;
        // A private key block first: it is the largest match, and its body would otherwise feed the shape rules.
        var result = PrivateKeyBlock().Replace(text, Redacted);
        result = Bearer().Replace(result, "${1}" + Redacted);
        result = LabelledValue().Replace(result, LabelledReplacement);
        result = AwsAccessKey().Replace(result, "${1}" + Redacted);
        result = GitHubToken().Replace(result, "${1}" + Redacted);
        result = GitHubFineGrained().Replace(result, "${1}" + Redacted);
        result = SlackToken().Replace(result, "${1}" + Redacted);
        result = SkKey().Replace(result, "${1}" + Redacted);
        result = Jwt().Replace(result, "${1}" + Redacted);
        return result;
    }

    // Keeps the label and the quotes; leaves an already redacted value, a bare Authorization scheme (the bearer rule took
    // its credential) and the JSON literals alone.
    private static string LabelledReplacement(Match match)
    {
        var value = match.Groups["value"];
        if (value.Value is Redacted or "null" or "true" or "false" || IsScheme(value.Value))
            return match.Value;
        return string.Concat(match.Value.AsSpan(0, value.Index - match.Index), Redacted,
            match.Value.AsSpan(value.Index - match.Index + value.Length));
    }

    private static bool IsScheme(string value) =>
        value.Equals("bearer", StringComparison.OrdinalIgnoreCase) || value.Equals("basic", StringComparison.OrdinalIgnoreCase)
        || value.Equals("token", StringComparison.OrdinalIgnoreCase) || value.Equals("digest", StringComparison.OrdinalIgnoreCase);

    // The header must be followed by key material, so text that only names the header keeps what follows; a block cut short
    // (no END line) loses the rest of the text.
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----\s*[A-Za-z0-9+/=]{16,}[\s\S]*?(?:-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"(\bbearer\s+)([A-Za-z0-9._~+/=-]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    // key=value, key: value, "key": "value", export KEY='value': a label naming a credential, the closing quote of a quoted
    // label, a separator, then a quoted value to its closing quote or a bare value to whitespace or punctuation.
    [GeneratedRegex(
        """(?<label>\b[A-Za-z0-9_.-]*(?:api[_-]?key|access[_-]?key|secret|token|password|passwd|private[_-]?key|client[_-]?secret)["'`]?\s*[:=]\s*)(?:"(?<value>[^"\r\n]*)"|'(?<value>[^'\r\n]*)'|(?<value>[^\s,;"'`}\]&)\[][^\s,;"'`}\]&)]*))""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelledValue();

    [GeneratedRegex(@"\b(AKIA|ASIA|ABIA|ACCA)[A-Z0-9]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(gh[pousr]_)[A-Za-z0-9]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\b(github_pat_)[A-Za-z0-9_]{22,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubFineGrained();

    [GeneratedRegex(@"\b(xox[abposr]-)[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackToken();

    // Anthropic sk-ant-, OpenAI sk-proj- and plain sk- keys.
    [GeneratedRegex(@"\b(sk-)[A-Za-z0-9_-]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex SkKey();

    [GeneratedRegex(@"\b(eyJ)[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();
}
