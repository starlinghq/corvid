using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Corvid.Vault;

/// <summary>
/// A sandbox's own CA (V-003a) and the leaf certificates it mints, one per host the sandbox CONNECTs to.
/// The CA key is an in-memory <see cref="ECDsa"/> and nothing else: no certificate object ever carries it, and it is
/// never exported, written or serialised. Only <see cref="CaCertificatePem"/>, the public certificate, leaves Vault.
/// </summary>
internal sealed class SandboxCertificates : IDisposable
{
    /// <summary>Hosts per sandbox; a CONNECT to one more is refused with <c>sandbox_limit</c>.</summary>
    public const int MaxHosts = 128;

    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1", "Server Authentication");

    private readonly ECDsa _caKey;
    private readonly X509SignatureGenerator _signer;
    private readonly Dictionary<string, Leaf> _leaves = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _disposed;

    public SandboxCertificates(string sandboxId, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        NotBefore = now.AddHours(-1);
        NotAfter = expiresAt.AddHours(1);
        _caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _signer = X509SignatureGenerator.CreateForECDsa(_caKey);
        var subject = new X500DistinguishedName($"CN=Corvid Vault sandbox CA {sandboxId}");
        var request = new CertificateRequest(subject, _caKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // Create, not CreateSelfSigned: the result is public-only, so the ECDsa above is the only holder of the key.
        Ca = request.Create(subject, _signer, NotBefore, NotAfter, Serial());
        CaCertificatePem = Ca.ExportCertificatePem();
    }

    /// <summary>The CA certificate, without its private key.</summary>
    public X509Certificate2 Ca { get; }

    public string CaCertificatePem { get; }

    public DateTimeOffset NotBefore { get; }

    public DateTimeOffset NotAfter { get; }

    /// <summary>
    /// The cached TLS context for <paramref name="host"/>, minting its leaf on first use. Null when the sandbox already
    /// has <see cref="MaxHosts"/> leaves or has been disposed. <paramref name="host"/> must be a validated, lower-case DNS name.
    /// </summary>
    public SslStreamCertificateContext? For(string host)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_leaves.TryGetValue(host, out Leaf? existing))
            {
                return existing.Context;
            }

            if (_leaves.Count >= MaxHosts)
            {
                return null;
            }

            Leaf leaf = Mint(host);
            _leaves[host] = leaf;
            return leaf.Context;
        }
    }

    /// <summary>The leaf already minted for <paramref name="host"/>, if any (tests and the SNI check).</summary>
    public SslStreamCertificateContext? Existing(string host)
    {
        lock (_gate)
        {
            return _leaves.TryGetValue(host, out Leaf? leaf) ? leaf.Context : null;
        }
    }

    private Leaf Mint(string host)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var request = new CertificateRequest(new X500DistinguishedName($"CN={host}"), key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(host);
            request.CertificateExtensions.Add(san.Build(critical: false));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], false));
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
            using X509Certificate2 unkeyed = request.Create(Ca.SubjectName, _signer, NotBefore, NotAfter, Serial());
            X509Certificate2 leaf = unkeyed.CopyWithPrivateKey(key);
            var context = SslStreamCertificateContext.Create(leaf, [Ca], offline: true);
            return new Leaf(leaf, key, context);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>16 random bytes, the top bits fixed at 01 so the DER integer is positive and exactly 16 bytes long.</summary>
    private static byte[] Serial()
    {
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x3F) | 0x40);
        return serial;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Leaf leaf in _leaves.Values)
            {
                leaf.Certificate.Dispose();
                leaf.Key.Dispose();
            }

            _leaves.Clear();
            Ca.Dispose();
            _caKey.Dispose();
        }
    }

    private sealed record Leaf(X509Certificate2 Certificate, ECDsa Key, SslStreamCertificateContext Context);
}
