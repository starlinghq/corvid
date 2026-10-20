using Corvid.Vault.Contracts;

namespace Corvid.Vault.Client;

/// <summary>Where a request carries its credential, for example <c>x-api-key</c> or <c>Authorization: Bearer</c>.</summary>
/// <param name="Header">The header that carries the credential.</param>
/// <param name="Scheme">A prefix such as <c>Bearer</c>, or null for the bare value.</param>
public sealed record SecretHeader(SecretRef Secret, string Header, string? Scheme = null)
{
    public string Render(string placeholder) => Scheme is null ? placeholder : Scheme + " " + placeholder;
}

/// <summary>
/// Placeholders for one Vault session, minted on first use and renewed a minute before they expire (vault.md §4). A
/// placeholder is not a secret: it is meaningless off the machine and outside this session.
/// </summary>
public sealed class VaultPlaceholders
{
    private static readonly TimeSpan Renewal = TimeSpan.FromMinutes(1);

    private readonly Func<SecretRef, CancellationToken, Task<PlaceholderResponse>> _mint;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PlaceholderResponse> _cache = new(StringComparer.Ordinal);

    public VaultPlaceholders(VaultClient client, TimeProvider? time = null)
        : this((secret, token) => client.RequestPlaceholderAsync(secret, token), time)
    {
        ArgumentNullException.ThrowIfNull(client);
    }

    public VaultPlaceholders(Func<SecretRef, CancellationToken, Task<PlaceholderResponse>> mint, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(mint);
        _mint = mint;
        _time = time ?? TimeProvider.System;
    }

    public async Task<string> GetAsync(SecretRef secret, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(secret.Name, out var cached) && cached.ExpiresAt - Renewal > _time.GetUtcNow())
                return cached.Placeholder;
        }
        var minted = await _mint(secret, cancellationToken).ConfigureAwait(false);
        if (!Placeholders.Contains(minted.Placeholder))
            throw new VaultException(VaultErrorCodes.BadPlaceholder, $"Vault returned something that is not a placeholder for '{secret.Name}'.");
        lock (_gate)
            _cache[secret.Name] = minted;
        return minted.Placeholder;
    }

    /// <summary>Drops a placeholder Vault no longer accepts, so the next request mints a fresh one.</summary>
    public void Forget(SecretRef secret)
    {
        lock (_gate)
            _cache.Remove(secret.Name);
    }
}

/// <summary>
/// Puts a Vault placeholder for one secret into one header of every request (vault.md §9: a model provider's key is a
/// secret like any other), then passes the request on to a <see cref="VaultHandler"/>, which sends it through Vault. Any
/// other credential header the provider SDK added (from its own defaults or environment) is removed first, so only the
/// placeholder leaves Corvid. A placeholder Vault rejects as unknown or expired is minted again and the request retried once.
/// </summary>
public sealed class VaultSecretHandler : DelegatingHandler
{
    // Headers that carry credentials for the providers Corvid speaks to. All are cleared before the placeholder is set.
    private static readonly string[] CredentialHeaders = ["Authorization", "x-api-key", "api-key", "x-goog-api-key", "Proxy-Authorization"];

    private readonly VaultPlaceholders _placeholders;
    private readonly SecretHeader _header;

    public VaultSecretHandler(VaultPlaceholders placeholders, SecretHeader header, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(placeholders);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.Header);
        _placeholders = placeholders;
        _header = header;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Buffer the body so a retry after a fresh placeholder can send it again.
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            await SetAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (VaultException exception) when (exception.Code == VaultErrorCodes.BadPlaceholder && attempt == 1)
            {
                _placeholders.Forget(_header.Secret);
            }
        }
    }

    private async Task SetAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        foreach (var name in CredentialHeaders.Append(_header.Header))
            request.Headers.Remove(name);
        var placeholder = await _placeholders.GetAsync(_header.Secret, cancellationToken).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation(_header.Header, _header.Render(placeholder));
    }
}
