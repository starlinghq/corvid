using System.Collections.Concurrent;
using System.Security.Cryptography;
using Corvid.Vault.Contracts;

namespace Corvid.Vault;

/// <summary>Placeholders minted for sessions. A placeholder resolves only for the session and peer uid that minted it.</summary>
internal sealed class PlaceholderRegistry(TimeProvider time)
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public (string Placeholder, DateTimeOffset ExpiresAt) Mint(string secret, string sessionId, uint uid, TimeSpan lifetime)
    {
        Sweep();
        string placeholder = Placeholders.Format(secret, RandomNumberGenerator.GetString(Alphabet, Placeholders.TokenLength));
        DateTimeOffset expires = time.GetUtcNow() + lifetime;
        _entries[placeholder] = new Entry(secret, sessionId, uid, expires);
        return (placeholder, expires);
    }

    /// <summary>The secret a placeholder stands for, or null if it is unknown, expired, or minted for another session or peer.</summary>
    public string? Resolve(PlaceholderMatch match, string sessionId, uint uid)
    {
        if (!_entries.TryGetValue(match.Value, out Entry? entry))
        {
            return null;
        }

        if (entry.ExpiresAt <= time.GetUtcNow())
        {
            _entries.TryRemove(match.Value, out _);
            return null;
        }

        return entry.SessionId == sessionId && entry.Uid == uid && entry.Secret == match.SecretName ? entry.Secret : null;
    }

    private void Sweep()
    {
        DateTimeOffset now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    private sealed record Entry(string Secret, string SessionId, uint Uid, DateTimeOffset ExpiresAt);
}
