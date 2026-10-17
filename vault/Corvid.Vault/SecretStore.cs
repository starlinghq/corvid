using System.Security.Cryptography;
using System.Text.Json;

namespace Corvid.Vault;

internal sealed record StoredSecret(byte[] Value, string Description, DateTimeOffset Created);

/// <summary>
/// The encrypted store: <c>CVS1</c> magic, a 12-byte nonce, a 16-byte tag, then AES-256-GCM ciphertext of the JSON
/// document. Rewritten atomically on every change; reloaded when another process (the CLI) changes it.
/// </summary>
internal sealed class SecretStore
{
    private static readonly byte[] Magic = "CVS1"u8.ToArray();
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly string _path;
    private readonly byte[] _key;
    private readonly Lock _gate = new();
    private Dictionary<string, StoredSecret> _secrets = new(StringComparer.Ordinal);
    private DateTime _loadedWriteTime;

    public SecretStore(string path, byte[] key)
    {
        _path = path;
        _key = key;
        Load();
    }

    /// <summary>Incremented whenever the contents change, so derived state (the scrubber) can rebuild.</summary>
    public int Version { get; private set; }

    public IReadOnlyDictionary<string, StoredSecret> Snapshot()
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return new Dictionary<string, StoredSecret>(_secrets, StringComparer.Ordinal);
        }
    }

    public bool TryGet(string name, out StoredSecret secret)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return _secrets.TryGetValue(name, out secret!);
        }
    }

    public void Set(string name, byte[] value, string description)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            _secrets[name] = new StoredSecret(value, description, DateTimeOffset.UtcNow);
            Save();
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            if (!_secrets.Remove(name))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    private void ReloadIfChanged()
    {
        DateTime writeTime = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default;
        if (writeTime != _loadedWriteTime)
        {
            Load();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _secrets = new(StringComparer.Ordinal);
            _loadedWriteTime = default;
            Version++;
            return;
        }

        byte[] file = File.ReadAllBytes(_path);
        int header = Magic.Length + NonceBytes + TagBytes;
        if (file.Length < header || !file.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{_path} is not a Corvid Vault store.");
        }

        ReadOnlySpan<byte> nonce = file.AsSpan(Magic.Length, NonceBytes);
        ReadOnlySpan<byte> tag = file.AsSpan(Magic.Length + NonceBytes, TagBytes);
        ReadOnlySpan<byte> cipher = file.AsSpan(header);
        byte[] plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain, Magic);
            StoreDocument document = JsonSerializer.Deserialize(plain, VaultInternalJsonContext.Default.StoreDocument)
                ?? new StoreDocument();
            _secrets = document.Secrets.ToDictionary(
                kv => kv.Key, kv => new StoredSecret(kv.Value.Value, kv.Value.Description, kv.Value.Created), StringComparer.Ordinal);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new InvalidDataException($"{_path} could not be decrypted with this master key.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        _loadedWriteTime = File.GetLastWriteTimeUtc(_path);
        Version++;
    }

    private void Save()
    {
        var document = new StoreDocument
        {
            Secrets = _secrets.ToDictionary(kv => kv.Key, kv => new StoreEntry
            {
                Value = kv.Value.Value,
                Description = kv.Value.Description,
                Created = kv.Value.Created,
            }),
        };
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(document, VaultInternalJsonContext.Default.StoreDocument);
        byte[] file = new byte[Magic.Length + NonceBytes + TagBytes + plain.Length];
        try
        {
            Magic.CopyTo(file, 0);
            Span<byte> nonce = file.AsSpan(Magic.Length, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(_key, TagBytes);
            aes.Encrypt(nonce, plain, file.AsSpan(Magic.Length + NonceBytes + TagBytes), file.AsSpan(Magic.Length + NonceBytes, TagBytes), Magic);
            PrivateDirectory.WriteAtomically(_path, file);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        _loadedWriteTime = File.GetLastWriteTimeUtc(_path);
        Version++;
    }
}

internal sealed class StoreDocument
{
    public Dictionary<string, StoreEntry> Secrets { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class StoreEntry
{
    public byte[] Value { get; set; } = [];

    public string Description { get; set; } = "";

    public DateTimeOffset Created { get; set; }
}
