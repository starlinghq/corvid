using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Corvid.Vault.Contracts;

namespace Corvid.Vault;

internal readonly record struct PeerInfo(int Pid, uint Uid);

/// <summary>Everything Vault does for a request, independent of the socket.</summary>
internal sealed class VaultCore : IDisposable
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Content-Length", "Transfer-Encoding", "TE", "Trailer", "Upgrade",
        "Proxy-Authorization", "Proxy-Authenticate", "Proxy-Connection",
    };

    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly Budgets _budgets;
    private readonly ConcurrentDictionary<uint, Queue<DateTimeOffset>> _scrubCalls = new();
    private readonly Lock _scrubberGate = new();
    private Scrubber _scrubber = Scrubber.Empty;
    private int _scrubberVersion = -1;

    public VaultCore(VaultOptions options, TimeProvider? time = null, HttpMessageHandler? upstream = null)
    {
        _time = time ?? TimeProvider.System;
        PrivateDirectory.Ensure(options.DataDirectory);
        byte[] key = MasterKey.LoadOrCreate(options);
        Store = new SecretStore(options.StorePath, key);
        Policy = new PolicySource(options.ConfigPath);
        Placeholders = new PlaceholderRegistry(_time);
        Audit = new AuditLog(options.AuditPath);
        _budgets = new Budgets(_time);
        _http = new HttpClient(
            upstream ?? new SocketsHttpHandler
            {
                // A redirect could carry the substituted header to another host; the client sees the 3xx instead.
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                // Decompress so scrubbing sees the real bytes; a compressed echo of a token would slip past.
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            },
            disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
    }

    public SecretStore Store { get; }

    public PolicySource Policy { get; }

    public PlaceholderRegistry Placeholders { get; }

    public AuditLog Audit { get; }

    public Scrubber CurrentScrubber
    {
        get
        {
            lock (_scrubberGate)
            {
                var snapshot = Store.Snapshot();
                if (Store.Version != _scrubberVersion)
                {
                    _scrubber = Scrubber.Build(snapshot);
                    _scrubberVersion = Store.Version;
                }

                return _scrubber;
            }
        }
    }

    public async Task<VaultResponse> HandleAsync(VaultRequest request, PeerInfo peer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return Error(VaultErrorCodes.BadRequest, "session_id is required.");
        }

        return request switch
        {
            CatalogRequest => Catalog(),
            PlaceholderRequest p => Mint(p, peer),
            ScrubRequest s => Scrub(s, peer),
            AuditSummaryRequest => new AuditSummaryResponse { Counts = Audit.Counts },
            ForwardRequest f => await ForwardAsync(f, peer, cancellationToken).ConfigureAwait(false),
            _ => Error(VaultErrorCodes.BadRequest, "Unknown request."),
        };
    }

    private CatalogResponse Catalog()
    {
        PolicyDocument policy = Policy.Current;
        var entries = Store.Snapshot()
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                policy.Secrets.TryGetValue(kv.Key, out SecretPolicy? p);
                return new CatalogEntry(kv.Key, kv.Value.Description, p?.Hosts ?? [], p?.Toolsets ?? []);
            })
            .ToList();
        return new CatalogResponse { Secrets = entries };
    }

    private VaultResponse Mint(PlaceholderRequest request, PeerInfo peer)
    {
        if (!Contracts.Placeholders.IsValidSecretName(request.Secret) || !Store.TryGet(request.Secret, out _))
        {
            AuditRefusal("placeholder", VaultErrorCodes.UnknownSecret, request, peer, request.Secret);
            return Error(VaultErrorCodes.UnknownSecret, $"No secret named '{request.Secret}'.");
        }

        var lifetime = TimeSpan.FromMinutes(Policy.Current.PlaceholderLifetimeMinutes);
        var (placeholder, expires) = Placeholders.Mint(request.Secret, request.SessionId, peer.Uid, lifetime);
        Audit.Write(new AuditEvent
        {
            Time = _time.GetUtcNow(), Op = "placeholder", Decision = "allowed", Secret = request.Secret,
            Session = request.SessionId, Pid = peer.Pid, Uid = peer.Uid,
        });
        return new PlaceholderResponse { Placeholder = placeholder, ExpiresAt = expires };
    }

    private VaultResponse Scrub(ScrubRequest request, PeerInfo peer)
    {
        if (!TryCountScrub(peer.Uid, Policy.Current.ScrubRequestsPerMinute))
        {
            AuditRefusal("scrub", VaultErrorCodes.RateLimited, request, peer, null);
            return Error(VaultErrorCodes.RateLimited, "Too many scrub requests; slow down.");
        }

        var (text, hits) = CurrentScrubber.Scrub(request.Text);
        if (hits > 0)
        {
            Audit.Write(new AuditEvent
            {
                Time = _time.GetUtcNow(), Op = "scrub", Decision = "allowed", Session = request.SessionId,
                Pid = peer.Pid, Uid = peer.Uid, ScrubHits = hits,
            });
        }

        return new ScrubResponse { Text = text, Hits = hits };
    }

    private async Task<VaultResponse> ForwardAsync(ForwardRequest request, PeerInfo peer, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return Refuse(VaultErrorCodes.BadRequest, "The URL must be absolute http(s).", request, peer, null, null);
        }

        string destination = $"{request.Method} {uri.Scheme}://{uri.IdnHost}{uri.AbsolutePath}";
        PolicyDocument policy = Policy.Current;

        // Where every placeholder sits.
        var found = new List<(PlaceholderMatch Match, string Where)>();
        found.AddRange(Contracts.Placeholders.Find(request.Url).Select(m => (m, "url")));
        foreach (WireHeader header in request.Headers)
        {
            found.AddRange(Contracts.Placeholders.Find(header.Value).Select(m => (m, "header")));
            if (Contracts.Placeholders.Contains(header.Name))
            {
                return Refuse(VaultErrorCodes.PlaceholderOutsideHeaders, "A placeholder may not appear in a header name.", request, peer, null, destination);
            }
        }

        if (request.Body is { Length: > 0 } body && Contracts.Placeholders.Contains(Encoding.UTF8.GetString(body)))
        {
            return Refuse(VaultErrorCodes.PlaceholderOutsideHeaders, "Placeholders are substituted in headers only, not in the body.", request, peer, null, destination);
        }

        if (found.Count == 0)
        {
            return Refuse(VaultErrorCodes.BadRequest, "The request carries no placeholder; send it directly.", request, peer, null, destination);
        }

        if (found.GroupBy(f => f.Match.Value).Any(g => g.Count() > 1))
        {
            return Refuse(VaultErrorCodes.PlaceholderRepeated, "Each placeholder may appear once per request.", request, peer, null, destination);
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal); // placeholder -> secret
        foreach (var (match, where) in found)
        {
            string? secret = Placeholders.Resolve(match, request.SessionId, peer.Uid);
            if (secret is null)
            {
                return Refuse(VaultErrorCodes.BadPlaceholder, "A placeholder is unknown, expired, or belongs to another session.", request, peer, match.SecretName, destination);
            }

            if (where == "url" && !(policy.Secrets.TryGetValue(secret, out SecretPolicy? sp) && sp.AllowInUrl))
            {
                return Refuse(VaultErrorCodes.PlaceholderOutsideHeaders, $"'{secret}' is substituted in headers only.", request, peer, secret, destination);
            }

            resolved[match.Value] = secret;
        }

        foreach (string secret in resolved.Values.Distinct(StringComparer.Ordinal))
        {
            PolicyResult result = PolicyEvaluator.Evaluate(policy, secret, request.Method.ToUpperInvariant(), uri, request);
            if (!result.Allowed)
            {
                return Refuse(result.Code, result.Message, request, peer, secret, destination);
            }

            if (!_budgets.TryConsume(secret, policy.Secrets[secret].RequestsPerHour))
            {
                return Refuse(VaultErrorCodes.BudgetExceeded, $"'{secret}' has used its hourly budget.", request, peer, secret, destination);
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (placeholder, secret) in resolved)
        {
            if (!Store.TryGet(secret, out StoredSecret stored))
            {
                return Refuse(VaultErrorCodes.UnknownSecret, $"'{secret}' was removed.", request, peer, secret, destination);
            }

            values[placeholder] = Encoding.UTF8.GetString(stored.Value);
        }

        string secretNames = string.Join(",", resolved.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Uri target = new(Substitute(request.Url, values));
        if (target.Scheme != uri.Scheme || !string.Equals(target.IdnHost, uri.IdnHost, StringComparison.OrdinalIgnoreCase) || target.Port != uri.Port)
        {
            return Refuse(VaultErrorCodes.BadRequest, "Substitution may not change the scheme, host or port.", request, peer, secretNames, destination);
        }

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(request.Body);
        }

        foreach (WireHeader header in request.Headers)
        {
            if (HopByHop.Contains(header.Name))
            {
                continue;
            }

            string value = Substitute(header.Value, values);
            if (!message.Headers.TryAddWithoutValidation(header.Name, value))
            {
                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        Scrubber scrubber = CurrentScrubber;
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Refuse(VaultErrorCodes.UpstreamError, scrubber.Scrub(e.Message).Text, request, peer, secretNames, destination);
        }

        using (response)
        {
            byte[]? raw = await ReadLimitedAsync(response.Content, policy.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                return Refuse(VaultErrorCodes.ResponseTooLarge, $"The response exceeds {policy.MaxResponseBytes} bytes.", request, peer, secretNames, destination);
            }

            int hits = 0;
            var headers = new List<WireHeader>();
            foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
            {
                if (HopByHop.Contains(header.Key))
                {
                    continue;
                }

                foreach (string value in header.Value)
                {
                    var (clean, n) = scrubber.Scrub(value);
                    hits += n;
                    headers.Add(new WireHeader(header.Key, clean));
                }
            }

            var (cleanBody, bodyHits) = scrubber.Scrub(raw);
            hits += bodyHits;
            Audit.Write(new AuditEvent
            {
                Time = _time.GetUtcNow(), Op = "forward", Decision = "allowed", Secret = secretNames, Session = request.SessionId,
                Pid = peer.Pid, Uid = peer.Uid, Destination = destination, Status = (int)response.StatusCode,
                Bytes = raw.LongLength, ScrubHits = hits,
            });
            return new ForwardResponse
            {
                Status = (int)response.StatusCode,
                Reason = response.ReasonPhrase is null ? null : scrubber.Scrub(response.ReasonPhrase).Text,
                Headers = headers,
                Body = cleanBody,
                ScrubHits = hits,
            };
        }
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        var builder = new StringBuilder(text.Length);
        int copied = 0;
        foreach (PlaceholderMatch match in Contracts.Placeholders.Find(text))
        {
            builder.Append(text, copied, match.Index - copied).Append(values[match.Value]);
            copied = match.Index + match.Length;
        }

        return builder.Append(text, copied, text.Length - copied).ToString();
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private bool TryCountScrub(uint uid, int perMinute)
    {
        Queue<DateTimeOffset> calls = _scrubCalls.GetOrAdd(uid, _ => new Queue<DateTimeOffset>());
        lock (calls)
        {
            DateTimeOffset now = _time.GetUtcNow();
            while (calls.Count > 0 && now - calls.Peek() > TimeSpan.FromMinutes(1))
            {
                calls.Dequeue();
            }

            if (calls.Count >= perMinute)
            {
                return false;
            }

            calls.Enqueue(now);
            return true;
        }
    }

    private ErrorResponse Refuse(string code, string message, VaultRequest request, PeerInfo peer, string? secret, string? destination)
    {
        Audit.Write(new AuditEvent
        {
            Time = _time.GetUtcNow(), Op = "forward", Decision = "refused", Code = code, Secret = secret,
            Session = request.SessionId, Pid = peer.Pid, Uid = peer.Uid, Destination = destination,
        });
        return Error(code, message);
    }

    private void AuditRefusal(string op, string code, VaultRequest request, PeerInfo peer, string? secret) =>
        Audit.Write(new AuditEvent
        {
            Time = _time.GetUtcNow(), Op = op, Decision = "refused", Code = code, Secret = secret,
            Session = request.SessionId, Pid = peer.Pid, Uid = peer.Uid,
        });

    private static ErrorResponse Error(string code, string message) => new() { Code = code, Message = message };

    public void Dispose() => _http.Dispose();
}
