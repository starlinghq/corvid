using Corvid.Vault.Contracts;

namespace Corvid.Vault.Client;

/// <summary>
/// For APIs that carry the credential in the URL path, such as Telegram's <c>https://api.telegram.org/bot&lt;token&gt;/...</c>
/// (vault.md §9). The caller writes <see cref="Marker"/> where the token goes; this handler replaces it with a Vault
/// placeholder for one secret and passes the request on to a <see cref="VaultHandler"/>, which sends it through Vault. Vault
/// substitutes in a URL only for a secret whose policy has <c>allowInUrl</c>. Every credential header is removed, a request
/// without the marker is refused rather than sent unauthenticated by mistake, and a placeholder Vault rejects as unknown or
/// expired is minted again and the request retried once.
/// </summary>
public sealed class VaultUrlSecretHandler : DelegatingHandler
{
    /// <summary>What a caller writes in the path where the secret goes. Never a secret.</summary>
    public const string Marker = "CORVID-VAULT-SECRET";

    private static readonly string[] CredentialHeaders = ["Authorization", "x-api-key", "api-key", "x-goog-api-key", "Proxy-Authorization"];

    private readonly VaultPlaceholders _placeholders;
    private readonly SecretRef _secret;

    public VaultUrlSecretHandler(VaultPlaceholders placeholders, SecretRef secret, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(placeholders);
        _placeholders = placeholders;
        _secret = secret;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = request.RequestUri?.OriginalString ?? throw new InvalidOperationException("The request has no URI.");
        if (!template.Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The request does not say where its secret goes; it was not sent.");
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        foreach (var name in CredentialHeaders)
            request.Headers.Remove(name);
        for (var attempt = 1; ; attempt++)
        {
            var placeholder = await _placeholders.GetAsync(_secret, cancellationToken).ConfigureAwait(false);
            request.RequestUri = new Uri(template.Replace(Marker, placeholder, StringComparison.Ordinal));
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (VaultException exception) when (exception.Code == VaultErrorCodes.BadPlaceholder && attempt == 1)
            {
                _placeholders.Forget(_secret);
            }
        }
    }
}
