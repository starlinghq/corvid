using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Remote;

/// <summary>Where a window keeps the device token pairing gave it: a 0600 file on the desktop, the browser's storage in the browser.</summary>
public interface IDeviceTokenStore
{
    string? Get(string address);

    void Set(string address, string token);
}

/// <summary>The desktop's device tokens, in <see cref="AttachedPaths.ClientTokensFile"/> (0600).</summary>
public sealed class FileDeviceTokenStore(string? path = null) : IDeviceTokenStore
{
    private readonly string _path = path ?? AttachedPaths.ClientTokensFile;

    public string? Get(string address) => ClientTokens.Get(_path, address);

    public void Set(string address, string token) => ClientTokens.Set(_path, address, token);
}

/// <summary>
/// How a window attaches (G-1), before it opens: pair first when given a code (keeping the device token), then connect with
/// the token kept for the address, or on this machine the owner token (<see cref="OwnerTokens"/>). What comes back is the
/// window's services: the attached engine's sessions, or, when it cannot attach, every area saying why (the window still
/// opens, as the in-process engine's does when it cannot start).
/// </summary>
public static class AttachedStartup
{
    /// <summary>The result: the services, the live connection (null when it failed) and a line for the log.</summary>
    public sealed record Attached(IAppServices Services, RemoteSessionService? Connection, string Report);

    /// <param name="dispatcher">The window's dispatcher, bound later (<see cref="LateDispatcher.Bind"/>) once the window exists.</param>
    /// <param name="ownerTokenFile">Where the owner token is on this machine; null where there is none (a browser).</param>
    public static async Task<Attached> ConnectAsync(string addressText, string? pairingCode, string device, IDispatcher dispatcher,
        IDeviceTokenStore tokens, string? ownerTokenFile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!AttachedAddress.TryParse(addressText, out var address, out var error))
            return Refused(error!);
        try
        {
            if (pairingCode is { Length: > 0 } code)
            {
                var paired = await AttachedClient.PairAsync(address, code, device, cancellationToken).ConfigureAwait(false);
                tokens.Set(address.Display, paired);
            }
            var token = tokens.Get(address.Display);
            if (token is null && address.IsLocal && ownerTokenFile is not null)
                token = OwnerTokens.Find(ownerTokenFile);
            if (token is null)
                return Refused($"No token for {address.Display}: pair this window first (corvid serve pair there, then --pair <code>).");
            var connection = await AttachedClient.ConnectAsync(address, token, device, dispatcher, cancellationToken).ConfigureAwait(false);
            var who = connection.Capabilities.Role == AttachedRole.Owner ? "as the owner" : $"as device '{connection.Capabilities.Device}'";
            return new Attached(new RemoteAppServices(connection, address), connection,
                $"attached to {address.Display} ({connection.Capabilities.Host ?? "the engine"}) {who}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refused($"Corvid at {address.Display} refused this window: {SafeText.Clean(exception.Message)}");
        }
        catch (IOException exception)
        {
            return Refused(SafeText.Clean(exception.Message));
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or OperationCanceledException)
        {
            return Refused($"Cannot attach to {address.Display}: {SafeText.Clean(exception.Message)}");
        }

        static Attached Refused(string reason) => new(new UnavailableAppServices(reason), null, reason);
    }
}
