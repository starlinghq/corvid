using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Corvid.Sandbox.Launcher;

/// <summary>
/// Relay mode (V-003b): stage 2, re-executed inside the sandbox after Landlock and seccomp, as the command's parent. It
/// starts the command with exactly the argv, environment and standard streams a direct exec would give it, then bridges
/// 127.0.0.1:<see cref="Port"/> to Corvid Vault's per-sandbox proxy socket until the command exits, and exits with its
/// status. It parses nothing: Vault enforces the protocol (CONNECT to a DNS name on 443, TLS against the sandbox CA).
/// </summary>
internal static class Relay
{
    /// <summary>Fixed: the network namespace is new, so the port is free, and the environment can be built before start.</summary>
    public const int Port = 3128;

    public const int MaxConnections = 32;

    private const int BufferSize = 16 * 1024;

    public static unsafe int Run(RelayRequest request)
    {
        // The command must not inherit the listener.
        if (Native.fcntl(request.ListenFd, Native.FSetFd, Native.FdCloexec) != 0)
        {
            Console.Error.WriteLine($"corvid-sandbox: the proxy relay's listener is not open (errno {Native.Errno}).");
            return 126;
        }

        string? path = Stage2.Resolve(request.Command[0], request.Environment);
        if (path is null)
        {
            Console.Error.WriteLine($"corvid-sandbox: {request.Command[0]}: command not found");
            return 127;
        }

        int pid;
        int error = Spawn(path, request.Command, request.Environment, out pid);
        if (error != 0)
        {
            Console.Error.WriteLine($"corvid-sandbox: cannot execute {path}: errno {error}");
            return error == Native.ENOENT ? 127 : 126;
        }

        // The command holds the real standard streams; the relay lets go of them, so whoever reads the command's output
        // sees end of file when the command (not the relay) closes it.
        int devNull = Native.open("/dev/null", Native.ORdwr | Native.OCloexec);
        if (devNull >= 0)
        {
            for (int fd = 0; fd <= 2; fd++)
            {
                Native.dup2(devNull, fd);
            }

            Native.close(devNull);
        }

        var listener = new Socket(new SafeSocketHandle(request.ListenFd, ownsHandle: true));
        _ = Task.Run(() => AcceptAsync(listener, request.Socket));
        return Wait(pid);
    }

    /// <summary>The command's exit status, or 128 + n when signal n killed it.</summary>
    private static unsafe int Wait(int pid)
    {
        int status = 0;
        while (Native.waitpid(pid, &status, 0) < 0)
        {
            if (Native.Errno != Native.EINTR)
            {
                return 126;
            }
        }

        int signal = status & 0x7f;
        return signal == 0 ? (status >> 8) & 0xff : 128 + signal;
    }

    private static unsafe int Spawn(string path, IReadOnlyList<string> argv, IReadOnlyList<string> environment, out int pid)
    {
        nint[] args = [.. argv.Select(Marshal.StringToCoTaskMemUTF8), 0];
        nint[] env = [.. environment.Select(Marshal.StringToCoTaskMemUTF8), 0];
        try
        {
            int child = 0;
            int error;
            fixed (nint* a = args, e = env)
            {
                error = Native.posix_spawn(&child, path, 0, 0, a, e);
            }

            pid = child;
            return error;
        }
        finally
        {
            foreach (nint p in args.Concat(env))
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private static async Task AcceptAsync(Socket listener, string socketPath)
    {
        using var slots = new SemaphoreSlim(MaxConnections);
        while (true)
        {
            await slots.WaitAsync().ConfigureAwait(false);
            Socket client;
            try
            {
                client = await listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (SocketException)
            {
                slots.Release();
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await BridgeAsync(client, socketPath).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            });
        }
    }

    /// <summary>Copies bytes both ways until both sides have finished, passing each end of stream on as a half-close.</summary>
    private static async Task BridgeAsync(Socket client, string socketPath)
    {
        using (client)
        using (var vault = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            try
            {
                await vault.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
                await Task.WhenAll(CopyAsync(client, vault), CopyAsync(vault, client)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
            {
                // Vault closed the sandbox, or the client went away; the connection simply ends.
            }
        }
    }

    private static async Task CopyAsync(Socket from, Socket to)
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            int read;
            while ((read = await from.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false)) > 0)
            {
                int sent = 0;
                while (sent < read)
                {
                    sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None).ConfigureAwait(false);
                }
            }

            to.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // One side reset: end both directions.
            try
            {
                from.Shutdown(SocketShutdown.Both);
                to.Shutdown(SocketShutdown.Both);
            }
            catch (Exception inner) when (inner is SocketException or ObjectDisposedException)
            {
            }
        }
    }
}
