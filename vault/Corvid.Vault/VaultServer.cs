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
        PrivateDirectory.Ensure(Path.GetDirectoryName(_socketPath)!);
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _listener.Listen(64);
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
            Native.UCred cred = Native.GetPeerCredentials(client);
            var peer = new PeerInfo(cred.Pid, cred.Uid);
            List<uint> allowed = _core.Policy.Current.AllowedUids;
            if (allowed.Count == 0 ? cred.Uid != _ownUid : !allowed.Contains(cred.Uid))
            {
                _core.Audit.Write(new AuditEvent { Time = DateTimeOffset.UtcNow, Op = "connect", Decision = "refused", Code = "peer_rejected", Pid = cred.Pid, Uid = cred.Uid });
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

    public void Dispose()
    {
        _listener?.Dispose();
        if (File.Exists(_socketPath))
        {
            File.Delete(_socketPath);
        }
    }
}
