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

    /// <summary>How long a window waits to attach before it opens refused (A6 review M3): a host that says nothing never hangs it.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

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
            // A6 review M3: the owner token goes only over a Unix socket whose server the kernel says is this user's
            // (AttachedClient checks SO_PEERCRED before sending anything). Over ws://, loopback included, anyone on the
            // machine could have taken the port; there the window pairs like any device.
            if (token is null && address.UnixSocket is not null && UnixPeers.CurrentUser is not null && ownerTokenFile is not null)
                token = OwnerTokens.Find(ownerTokenFile);
            if (token is null)
                return Refused($"No token for {address.Display}: pair this window first (corvid serve pair there, then --pair <code>)" +
                    (address.UnixSocket is null && ownerTokenFile is not null ? "; the owner token is sent only over the Unix socket." : "."));
            var connection = await AttachedClient.ConnectAsync(address, token, device, dispatcher, cancellationToken).ConfigureAwait(false);
            var who = connection.Capabilities.Role == AttachedRole.Owner ? "as the owner" : $"as device '{connection.Capabilities.Device}'";
            return new Attached(new RemoteAppServices(connection, address), connection,
                $"attached to {address.Display} ({connection.Capabilities.Host ?? "the engine"}) {who}");
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested && exception is OperationCanceledException or IOException)
        {
            // A6 review M3: a host that accepts the connection and then says nothing opens the window refused, not hung.
            return Refused($"Corvid at {address.Display} did not answer in time; the window opens without it.");
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
