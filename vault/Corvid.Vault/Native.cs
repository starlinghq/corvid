using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Corvid.Vault;

/// <summary>The Linux calls .NET has no managed API for.</summary>
internal static partial class Native
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int PrSetDumpable = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct UCred
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    /// <summary>The connecting process's pid, uid and gid, as the kernel recorded them at connect time.</summary>
    public static UCred GetPeerCredentials(Socket socket)
    {
        int size = Marshal.SizeOf<UCred>();
        if (getsockopt((int)socket.Handle, SolSocket, SoPeerCred, out UCred cred, ref size) != 0)
        {
            throw new InvalidOperationException($"getsockopt(SO_PEERCRED) failed: errno {Marshal.GetLastPInvokeError()}.");
        }

        return cred;
    }

    /// <summary>
    /// Clears the dumpable flag: no core dumps, and same-uid processes can no longer read /proc/self/mem or attach a
    /// debugger. Resets on execve, which is why exec children need the strong tier.
    /// </summary>
    public static bool TryMakeNonDumpable() =>
        OperatingSystem.IsLinux() && prctl(PrSetDumpable, 0, 0, 0, 0) == 0;

    public static uint CurrentUid() => getuid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int getsockopt(int sockfd, int level, int optname, out UCred optval, ref int optlen);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc")]
    private static partial uint getuid();
}
