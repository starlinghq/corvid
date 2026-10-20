using System.Net;
using System.Net.Sockets;
using Corvid.Models.Privacy;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Models.Remote;

/// <summary>Cloud models need Vault (plan §10.5): without it they are unavailable, never keyed some other way.</summary>
public sealed class VaultUnavailableException(string message) : InvalidOperationException(message)
{
    public VaultUnavailableException()
        : this("Vault is not available.")
    {
    }

    public VaultUnavailableException(string message, Exception innerException)
        : this(message + " " + innerException.Message)
    {
    }
}

/// <summary>Vault, as a remote binding uses it: the client for its session and the placeholders minted in that session.</summary>
public sealed record VaultAccess(VaultClient Client, VaultPlaceholders Placeholders)
{
    /// <summary>One Vault session for every model binding of this process; Vault binds placeholders to it and to this uid.</summary>
    public static VaultAccess Create(string socketPath, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var client = new VaultClient("corvid-models-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8], socketPath);
        return new VaultAccess(client, new VaultPlaceholders(client, time));
    }
}

/// <summary>
/// The HTTP pipeline under a remote model's SDK (plan §10.4, §10.5, vault.md §9). A model with a credential sends every
/// request through Vault's egress proxy with a placeholder in the credential header, so Corvid never holds the key. A cloud
/// model has no other route at all: below the Vault handler sits a handler that refuses, so a request that somehow lacked
/// the placeholder fails rather than leaving the machine unproxied. A keyless local server is reached directly.
/// </summary>
public static class ModelHttp
{
    /// <summary>The toolset model requests name in Vault's forward request, so a provider key can be limited to model calls.</summary>
    public const string Toolset = "model-provider";

    /// <summary>What a provider SDK is given as its key. Never a secret: the pipeline replaces it with a placeholder or removes it.</summary>
    public const string CredentialSentinel = "vault-placeholder";

    // Headers that carry credentials; a keyless local server receives none of them.
    private static readonly string[] CredentialHeaders = ["Authorization", "x-api-key", "api-key", "x-goog-api-key", "Proxy-Authorization"];

    /// <param name="network">The direct route for a local server (and a test's fake); ignored for cloud models.</param>
    public static HttpMessageHandler Create(ModelProfile profile, SecretHeader? credential, VaultAccess? vault, HttpMessageHandler? network = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (credential is null)
        {
            if (profile.Privacy == PrivacyClass.Cloud)
                throw new InvalidOperationException($"'{profile.Id}' is a cloud model without a Vault secret: cloud models authenticate only through Vault.");
            return new RequestContextHandler(new NoCredentials(network ?? Direct()));
        }
        if (vault is null)
            throw new VaultUnavailableException($"'{profile.Id}' needs the Vault secret '{credential.Secret.Name}', and Vault is not configured.");
        var below = profile.Privacy == PrivacyClass.Cloud ? new NoDirectRoute(profile.Id) : network ?? Direct();
        return new RequestContextHandler(new VaultSecretHandler(vault.Placeholders, credential, new VaultHandler(vault.Client, below)));
    }

    /// <summary>For local servers: no ambient proxy, no redirects (a redirect could lead a request elsewhere).</summary>
    public static HttpMessageHandler Direct() => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// Maps a failure under a provider SDK to what the slot's fallback chain understands (<see cref="FallbackErrors"/>): Vault
    /// throttling and upstream failures and an unreachable Vault or server fall back; a Vault refusal (an unbound host, a
    /// denied path, an approval Vault wants) is a client error that another provider would not fix. Returns null when the
    /// exception is not one of these.
    /// </summary>
    public static Exception? Translate(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PrivacyEgressRefusedException:
                    return current;
                case VaultException vault:
                    var status = vault.Code switch
                    {
                        VaultErrorCodes.RateLimited or VaultErrorCodes.BudgetExceeded => HttpStatusCode.TooManyRequests,
                        VaultErrorCodes.UpstreamError => HttpStatusCode.BadGateway,
                        _ => HttpStatusCode.Forbidden,
                    };
                    return new HttpRequestException($"Vault refused the model request: {vault.Message}", vault, status);
                case SocketException or IOException when exception is not HttpRequestException { StatusCode: not null }:
                    return new HttpRequestException($"The model endpoint or Vault could not be reached: {current.Message}", current);
            }
        }
        return null;
    }

    // The SDK insists on a key; a keyless local server must not receive the sentinel as one.
    private sealed class NoCredentials(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var name in CredentialHeaders)
                request.Headers.Remove(name);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class NoDirectRoute(string model) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"A request to cloud model '{model}' reached the network without Vault; it was not sent.");
    }

    // Tells Vault which calls these are and whether the turn holds outside content; Vault only ever tightens on taint.
    private sealed class RequestContextHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Options.Set(VaultHandler.Tainted, ModelRequestContext.Current?.Tainted ?? false);
            request.Options.Set(VaultHandler.Toolset, Toolset);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
