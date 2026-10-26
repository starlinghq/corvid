using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Corvid.App.Core.Remote;

/// <summary>A Unix socket was served by another user than this process's: nothing was sent to it (A6 review M3).</summary>
public sealed class PeerRefusedException(string message) : IOException(message);

/// <summary>
/// Who is on the other end of a Unix socket (A6 review M3), on Linux: the user the kernel recorded for the peer when it
/// connected (<c>SO_PEERCRED</c>), compared with this process's own (<c>/proc/self/status</c>). Elsewhere neither can be told
/// and both are null, so the owner token is not sent on its own (<see cref="AttachedStartup"/>).
/// </summary>
public static class UnixPeers
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>This process's effective user on Linux; null elsewhere or when it cannot be read.</summary>
    public static uint? CurrentUser { get; } = ReadCurrentUser();

    /// <summary>The peer's user of a connected Unix socket on Linux; null elsewhere or when the kernel does not say.</summary>
    public static uint? UserOf(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (!OperatingSystem.IsLinux() || socket.AddressFamily != AddressFamily.Unix)
            return null;
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
        Span<byte> credentials = stackalloc byte[12];
        try
        {
            return socket.GetRawSocketOption(SolSocket, SoPeerCred, credentials) >= 8 ? MemoryMarshal.Read<uint>(credentials[4..]) : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static uint? ReadCurrentUser()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            // "Uid:\t<real>\t<effective>\t<saved>\t<filesystem>": the effective one is who this process acts as.
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var fields = line["Uid:".Length..].Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                    return fields.Length > 1 && uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var effective) ? effective : null;
                }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
