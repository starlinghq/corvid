using System.Net.Sockets;
using Corvid.Vault.Contracts;

namespace Corvid.Vault;

/// <summary>
/// The Unix socket Corvid talks to. The socket lives in a 0700 directory and is itself 0600, and every connection is
/// checked with SO_PEERCRED against the allowed uids before any request is read.
/// </summary>
internal sealed class VaultServer : IDisposable
{
    private readonly VaultCore _core;
    private readonly string _socketPath;
    private readonly uint _ownUid = Native.CurrentUid();
    private Socket? _listener;

    public VaultServer(VaultCore core, string socketPath)
    {
        _core = core;
        _socketPath = socketPath;
    }

    /// <summary>Binds and listens now; returns the accept loop, which ends when <paramref name="cancellationToken"/> fires.</summary>
    public Task Start(CancellationToken cancellationToken)
    {
        _listener = Listen(_socketPath, 64);
        cancellationToken.Register(() => _listener.Dispose());
        return AcceptLoopAsync(cancellationToken);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException && cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            if (CheckPeer(_core, client, _ownUid, sandbox: null) is not { } peer)
            {
                return;
            }

            try
            {
                while (await VaultWire.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false) is { } request)
                {
                    VaultResponse response;
                    try
                    {
                        response = await _core.HandleAsync(request, peer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Exception text could in principle carry a value; scrub it like any other output.
                        response = new ErrorResponse { Code = VaultErrorCodes.BadRequest, Message = _core.CurrentScrubber.Scrub(e.Message).Text };
                    }

                    await VaultWire.WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException)
            {
                // Malformed frame, client gone, or shutting down: drop the connection.
            }
        }
    }

    /// <summary>
    /// The connecting process's credentials if its uid may talk to Vault: the policy's <c>allowedUids</c>, or Vault's own
    /// uid when that list is empty. A rejected peer is audited as a refused <c>connect</c> and gets null. Shared by the main
    /// socket and every sandbox proxy socket.
    /// </summary>
    internal static PeerInfo? CheckPeer(VaultCore core, Socket client, uint ownUid, string? sandbox)
    {
        Native.UCred cred = Native.GetPeerCredentials(client);
        List<uint> allowed = core.Policy.Current.AllowedUids;
        if (allowed.Count == 0 ? cred.Uid != ownUid : !allowed.Contains(cred.Uid))
        {
            core.Audit.Write(new AuditEvent
            {
                Time = DateTimeOffset.UtcNow, Op = "connect", Decision = "refused", Code = "peer_rejected", Pid = cred.Pid, Uid = cred.Uid,
                Sandbox = sandbox,
            });
            return null;
        }

        return new PeerInfo(cred.Pid, cred.Uid);
    }

    /// <summary>Binds a listening Unix socket at <paramref name="path"/>, mode 0600, in a 0700 directory.</summary>
    internal static Socket Listen(string path, int backlog)
    {
        PrivateDirectory.Ensure(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(backlog);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }
    }
}
