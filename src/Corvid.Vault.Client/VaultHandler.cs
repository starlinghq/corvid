using System.Net;
using Corvid.Vault.Contracts;

namespace Corvid.Vault.Client;

/// <summary>
/// An <see cref="HttpClient"/> handler for tools. A request with a Vault placeholder in its URL or headers is sent
/// through Vault, which substitutes the value, sends it and returns the response scrubbed. Any other request goes to
/// the inner handler unchanged. A placeholder-bearing request never reaches the inner handler.
/// </summary>
public sealed class VaultHandler : DelegatingHandler
{
    /// <summary>Set on <see cref="HttpRequestMessage.Options"/> when the user approved this request in Corvid.</summary>
    public static readonly HttpRequestOptionsKey<bool> Approved = new("corvid.vault.approved");

    /// <summary>Set on <see cref="HttpRequestMessage.Options"/> when the turn holds untrusted content.</summary>
    public static readonly HttpRequestOptionsKey<bool> Tainted = new("corvid.vault.tainted");

    /// <summary>Set on <see cref="HttpRequestMessage.Options"/> to the calling tool's toolset.</summary>
    public static readonly HttpRequestOptionsKey<string> Toolset = new("corvid.vault.toolset");

    private readonly VaultClient _client;

    public VaultHandler(VaultClient client)
        : this(client, new SocketsHttpHandler())
    {
    }

    public VaultHandler(VaultClient client, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<WireHeader> headers = [.. Flatten(request)];
        string url = request.RequestUri?.OriginalString ?? throw new InvalidOperationException("The request has no URI.");
        if (!Placeholders.Contains(url) && !headers.Exists(h => Placeholders.Contains(h.Value)))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        byte[]? body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        request.Options.TryGetValue(Approved, out bool approved);
        request.Options.TryGetValue(Tainted, out bool tainted);
        request.Options.TryGetValue(Toolset, out string? toolset);

        ForwardResponse forwarded = await _client.ForwardAsync(
            new ForwardRequest
            {
                SessionId = _client.SessionId,
                Method = request.Method.Method,
                Url = url,
                Headers = headers,
                Body = body,
                Approved = approved,
                Tainted = tainted,
                Toolset = toolset,
            },
            cancellationToken).ConfigureAwait(false);

        var response = new HttpResponseMessage((HttpStatusCode)forwarded.Status)
        {
            ReasonPhrase = forwarded.Reason,
            RequestMessage = request,
            Content = new ByteArrayContent(forwarded.Body),
        };
        foreach (WireHeader header in forwarded.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                response.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        return response;
    }

    private static IEnumerable<WireHeader> Flatten(HttpRequestMessage request)
    {
        foreach (var header in request.Headers.NonValidated)
        {
            foreach (string value in header.Value)
            {
                yield return new WireHeader(header.Key, value);
            }
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers.NonValidated)
            {
                foreach (string value in header.Value)
                {
                    yield return new WireHeader(header.Key, value);
                }
            }
        }
    }
}
