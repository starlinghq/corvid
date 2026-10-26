using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corvid.App.Core.Remote;

/// <summary>A pairing code the owner made, and until when it redeems.</summary>
public sealed record PairingCode(string Code, DateTimeOffset Expires);

/// <summary>A paired device as <see cref="AttachedPaths.DevicesFile"/> keeps it: its name, its token's digest, when.</summary>
public sealed record PairedDevice(string Name, string Digest, DateTimeOffset Paired);

/// <summary>
/// <c>corvid serve --attach</c>'s credentials (G-1). The owner token is checked by <paramref name="owner"/>: the Vault
/// secret <c>Corvid:Serve:Attach:TokenSecret</c> names when one is configured (Corvid never holds its value), or else the
/// token generated in <see cref="AttachedPaths.OwnerTokenFile"/> (0600). Paired devices' tokens are kept as SHA-256 digests in
/// <see cref="AttachedPaths.DevicesFile"/> (0600), read again at each check, so <c>corvid serve revoke</c> refuses a device's
/// next connection and closes the ones it has open (<see cref="IsCurrentAsync"/>). Pairing codes live in this process
/// only: one the owner made (<see cref="CreatePairingCode"/>, from the
/// desktop or <c>corvid serve pair</c>) redeems once before it expires. Past <see cref="FailuresPerMinute"/> wrong codes or
/// tokens in a minute from one peer, that peer is refused everything until the minute is over, and past
/// <see cref="NetworkFailuresPerMinute"/> from the network as a whole every network peer is, so a code cannot be guessed.
/// The Unix socket (only the owner's user can open it) is counted on its own and never shut by the network's guesses, so
/// a host on the LAN cannot lock the owner out of their own machine (A6 fix).
/// </summary>
public sealed class AttachedAuthenticator(Func<string, CancellationToken, ValueTask<bool?>> owner, string devicesFile, TimeProvider? time = null)
    : IAttachedAuthenticator
{
    /// <summary>How long a pairing code redeems.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Wrong codes and refused tokens allowed from one peer in a minute; past it the peer is refused until the minute ends.</summary>
    public const int FailuresPerMinute = 10;

    /// <summary>Wrong codes and refused tokens allowed from all network peers together in a minute (not the Unix socket).</summary>
    public const int NetworkFailuresPerMinute = 100;

    /// <summary>The peer a connection over the Unix socket is (the listener names TCP peers by their address).</summary>
    public const string SocketPeer = "the Unix socket";

    // Peers remembered at most: past it, peers with no failure in the last minute are forgotten.
    private const int MaxPeers = 1024;

    /// <summary>Codes outstanding at once; making another drops the oldest.</summary>
    public const int MaxCodes = 8;

    /// <summary>Devices paired at once.</summary>
    public const int MaxDevices = 64;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<(string Digest, DateTimeOffset Expires)> _codes = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _network = new();

    /// <summary>Makes a pairing code that redeems once within <see cref="CodeLifetime"/>.</summary>
    public PairingCode CreatePairingCode()
    {
        var code = AttachedSecrets.NewPairingCode();
        var expires = _time.GetUtcNow() + CodeLifetime;
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            if (_codes.Count >= MaxCodes)
                _codes.RemoveAt(0);
            _codes.Add((AttachedSecrets.Digest(AttachedSecrets.NormalizeCode(code)), expires));
        }
        return new PairingCode(code, expires);
    }

    ValueTask<PairingCode?> IAttachedAuthenticator.CreatePairingCodeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<PairingCode?>(CreatePairingCode());

    /// <summary>A check from the Unix socket (<see cref="SocketPeer"/>).</summary>
    public ValueTask<AttachedIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken) =>
        AuthenticateAsync(token, SocketPeer, cancellationToken);

    public async ValueTask<AttachedIdentity?> AuthenticateAsync(string token, string peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(peer);
        if (token.Length is 0 or > 4096 || Limited(peer))
            return null;
        var digest = AttachedSecrets.Digest(token);
        foreach (var device in ReadDevices(devicesFile))
            if (AttachedSecrets.FixedTimeEquals(device.Digest, digest))
                return new AttachedIdentity(AttachedRole.Device, device.Name);
        // Null (cannot be told: Vault is not answering) refuses, as a wrong token does, but is not counted as a guess.
        var isOwner = await owner(token, cancellationToken).ConfigureAwait(false);
        if (isOwner == true)
            return new AttachedIdentity(AttachedRole.Owner, null);
        if (isOwner == false)
            Fail(peer);
        return null;
    }

    /// <summary>A pairing from the Unix socket (<see cref="SocketPeer"/>).</summary>
    public ValueTask<(string Token, AttachedIdentity Identity)?> PairAsync(string code, string device, CancellationToken cancellationToken) =>
        PairAsync(code, device, SocketPeer, cancellationToken);

    public ValueTask<(string Token, AttachedIdentity Identity)?> PairAsync(string code, string device, string peer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(peer);
        var digest = AttachedSecrets.Digest(AttachedSecrets.NormalizeCode(code));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (TooMany(peer, now))
                return ValueTask.FromResult<(string, AttachedIdentity)?>(null);
            var index = _codes.FindIndex(entry => AttachedSecrets.FixedTimeEquals(entry.Digest, digest));
            if (index < 0)
            {
                Count(peer, now);
                return ValueTask.FromResult<(string, AttachedIdentity)?>(null);
            }
            // Redeemed under the gate, so it redeems once; the devices file is written outside it, so a slow lock on the
            // file never holds up another connection's check (A6 fix).
            _codes.RemoveAt(index);
        }
        var paired = PrivateFiles.Locked(devicesFile, () =>
        {
            var devices = ReadDevices(devicesFile).ToList();
            if (devices.Count >= MaxDevices)
                return ((string, AttachedIdentity)?)null;
            var name = device;
            for (var n = 2; devices.Any(existing => existing.Name == name); n++)
                name = $"{device} ({n})";
            var token = AttachedSecrets.NewToken();
            devices.Add(new PairedDevice(name, AttachedSecrets.Digest(token), now));
            WriteDevices(devicesFile, devices);
            return (token, new AttachedIdentity(AttachedRole.Device, name));
        });
        return ValueTask.FromResult(paired);
    }

    /// <summary>A device is current while its token's digest is in the devices file; the owner always is.</summary>
    public ValueTask<bool> IsCurrentAsync(AttachedIdentity identity, string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(token);
        if (identity.Role == AttachedRole.Owner)
            return ValueTask.FromResult(true);
        var digest = AttachedSecrets.Digest(token);
        return ValueTask.FromResult(ReadDevices(devicesFile).Any(device => AttachedSecrets.FixedTimeEquals(device.Digest, digest)));
    }

    /// <summary>The paired devices in the file; none when it does not exist.</summary>
    /// <exception cref="UnauthorizedAccessException">Others than its owner can read it.</exception>
    public static IReadOnlyList<PairedDevice> ReadDevices(string path)
    {
        var text = PrivateFiles.Read(path);
        if (string.IsNullOrWhiteSpace(text))
            return [];
        try
        {
            return JsonSerializer.Deserialize(text, AttachedFilesJson.Default.DevicesFile)?.Devices ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{path} is not a devices file: {exception.Message}", exception);
        }
    }

    /// <summary>Removes a paired device by name; false when none has it.</summary>
    public static bool Revoke(string path, string name) => PrivateFiles.Locked(path, () =>
    {
        var devices = ReadDevices(path).ToList();
        if (devices.RemoveAll(device => device.Name == name) == 0)
            return false;
        WriteDevices(path, devices);
        return true;
    });

    private static void WriteDevices(string path, IReadOnlyList<PairedDevice> devices) =>
        PrivateFiles.Write(path, JsonSerializer.Serialize(new DevicesFileShape([.. devices]), AttachedFilesJson.Default.DevicesFile));

    private bool Limited(string peer)
    {
        lock (_gate)
            return TooMany(peer, _time.GetUtcNow());
    }

    private void Fail(string peer)
    {
        lock (_gate)
            Count(peer, _time.GetUtcNow());
    }

    // With the gate held.
    private void Count(string peer, DateTimeOffset now)
    {
        if (_failures.Count >= MaxPeers && !_failures.ContainsKey(peer))
            foreach (var (known, queue) in _failures.ToList())
                if (Recent(queue, now) == 0)
                    _failures.Remove(known);
        if (!_failures.TryGetValue(peer, out var failures))
            _failures[peer] = failures = new Queue<DateTimeOffset>();
        failures.Enqueue(now);
        if (peer != SocketPeer)
            _network.Enqueue(now);
    }

    // With the gate held.
    private bool TooMany(string peer, DateTimeOffset now)
    {
        if (_failures.TryGetValue(peer, out var failures) && Recent(failures, now) >= FailuresPerMinute)
            return true;
        return peer != SocketPeer && Recent(_network, now) >= NetworkFailuresPerMinute;
    }

    private static int Recent(Queue<DateTimeOffset> failures, DateTimeOffset now)
    {
        while (failures.TryPeek(out var oldest) && oldest <= now - TimeSpan.FromMinutes(1))
            failures.Dequeue();
        return failures.Count;
    }

    private void Prune(DateTimeOffset now) => _codes.RemoveAll(entry => entry.Expires <= now);
}

/// <summary>The devices file's shape.</summary>
public sealed record DevicesFileShape(PairedDevice[] Devices);

/// <summary>A client's device tokens, by address: what pairing returned, kept to attach again (0600).</summary>
public static class ClientTokens
{
    /// <summary>The token kept for the address, or null.</summary>
    public static string? Get(string path, string address)
    {
        var text = PrivateFiles.Read(path);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonSerializer.Deserialize(text, AttachedFilesJson.Default.DictionaryStringString)?.GetValueOrDefault(address);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps the token for the address, replacing any earlier one.</summary>
    public static void Set(string path, string address, string token)
    {
        Dictionary<string, string> tokens;
        try
        {
            tokens = PrivateFiles.Read(path) is { Length: > 0 } text
                ? JsonSerializer.Deserialize(text, AttachedFilesJson.Default.DictionaryStringString) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            tokens = new(StringComparer.Ordinal);
        }
        tokens[address] = token;
        PrivateFiles.Write(path, JsonSerializer.Serialize(tokens, AttachedFilesJson.Default.DictionaryStringString));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(DevicesFileShape), TypeInfoPropertyName = "DevicesFile")]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AttachedFilesJson : JsonSerializerContext;
