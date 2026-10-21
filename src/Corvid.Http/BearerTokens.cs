using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Http;

/// <summary>Whether a presented bearer token is the configured one.</summary>
public interface IBearerTokenVerifier
{
    /// <returns>True or false; null when it cannot be told (Vault is not there), which refuses the request.</returns>
    ValueTask<bool?> VerifyAsync(string token, CancellationToken cancellationToken);
}

/// <summary>
/// Checks a bearer token against a Vault secret without Corvid ever holding the secret's value (plan §10.5). Vault has no
/// compare operation, so the token is sent to Vault's scrub service, which replaces any secret value it holds with
/// <c>[secret:name]</c>: the token is the secret exactly when the whole token comes back as that one marker for the
/// configured name.
/// </summary>
/// <remarks>
/// The scrubber also matches a secret's first 12 bytes (to catch truncated echoes), so a token must be longer than that,
/// and <see cref="MinimumBytes"/> leaves margin; a token holding the marker text itself is refused, since it would come back
/// unchanged. Accepted tokens are remembered for a few minutes by SHA-256 digest only, so steady traffic does not use up
/// Vault's per-user scrub budget, and failures are limited per minute so a flood cannot either.
/// </remarks>
public sealed class VaultTokenVerifier(string secret, string? socketPath = null, TimeProvider? time = null) : IBearerTokenVerifier
{
    public const int MinimumBytes = 24;
    public const int MaximumBytes = 4096;
    private const int FailuresPerMinute = 20;
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(5);

    private readonly VaultClient _vault = new("corvid-serve", socketPath);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _accepted = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DateTimeOffset> _failures = new();

    public string Secret { get; } = secret;

    /// <summary>Whether Vault holds the secret, checked when the endpoint starts so a typo is not a server that refuses everyone.</summary>
    public async Task<bool> SecretExistsAsync(CancellationToken cancellationToken) =>
        (await _vault.GetCatalogAsync(cancellationToken).ConfigureAwait(false)).Any(entry => entry.Name == Secret);

    public async ValueTask<bool?> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var bytes = Encoding.UTF8.GetByteCount(token);
        if (bytes is < MinimumBytes or > MaximumBytes || token.Contains('[', StringComparison.Ordinal) || token.Contains(']', StringComparison.Ordinal))
            return Fail();
        var now = _time.GetUtcNow();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (_accepted.TryGetValue(digest, out var until) && until > now)
            return true;
        if (TooManyFailures(now))
            return false;

        ScrubResponse scrubbed;
        try
        {
            scrubbed = await _vault.SendAsync<ScrubResponse>(new ScrubRequest { SessionId = _vault.SessionId, Text = token }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VaultException or IOException or SocketException or InvalidOperationException
                                              or TimeoutException)
        {
            return null;
        }
        if (scrubbed.Hits != 1 || !string.Equals(scrubbed.Text, $"[secret:{Secret}]", StringComparison.Ordinal))
            return Fail();
        _accepted[digest] = now + Remember;
        return true;
    }

    private bool Fail()
    {
        _failures.Enqueue(_time.GetUtcNow());
        return false;
    }

    private bool TooManyFailures(DateTimeOffset now)
    {
        while (_failures.TryPeek(out var oldest) && oldest < now - TimeSpan.FromMinutes(1))
            _failures.TryDequeue(out _);
        return _failures.Count >= FailuresPerMinute;
    }
}
