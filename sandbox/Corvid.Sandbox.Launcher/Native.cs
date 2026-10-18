using System.Runtime.InteropServices;

namespace Corvid.Sandbox.Launcher;

/// <summary>The libc calls .NET has no managed API for. Syscall numbers below 400 differ by architecture.</summary>
internal static unsafe partial class Native
{
    public const int EINTR = 4;
    public const int ENOENT = 2;
    public const int EACCES = 13;
    public const int ENOSYS = 38;
    public const int EOPNOTSUPP = 95;

    public const int SIGKILL = 9;
    public const int OWronly = 0x1;
    public const int OCreat = 0x40;
    public const int OTrunc = 0x200;
    public const int OCloexec = 0x80000;
    public const int OPath = 0x200000;

    /// <summary>O_NOFOLLOW: 0400000 in the generic table (x86_64), 0100000 on arm, arm64 and powerpc.</summary>
    public static int ONofollow => RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le
        ? 0x8000
        : 0x20000;
    public const int FSetFd = 2;
    public const int PollIn = 0x1;

    public const int PrSetNoNewPrivs = 38;
    public const int PrSetSeccomp = 22;
    public const int SeccompModeFilter = 2;

    // Numbered alike on every architecture (the unified table from 5.x on).
    public const long SysPidfdOpen = 434;
    public const long SysLandlockCreateRuleset = 444;
    public const long SysLandlockAddRule = 445;
    public const long SysLandlockRestrictSelf = 446;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>The canonical absolute path with every symlink resolved, or null when it does not exist.</summary>
    public static string? RealPath(string path)
    {
        nint resolved = realpath(path, 0);
        if (resolved == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    /// <summary>The account's home directory from the password database, which a changed HOME does not affect.</summary>
    public static string? PasswordHome()
    {
        // struct passwd { char *pw_name, *pw_passwd; uid_t pw_uid; gid_t pw_gid; char *pw_gecos, *pw_dir, *pw_shell; }
        nint entry = getpwuid(getuid());
        return entry == 0 ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry, (2 * nint.Size) + 8 + nint.Size));
    }

    public static int Errno => Marshal.GetLastPInvokeError();

    [LibraryImport("libc")]
    private static partial uint getuid();

    [LibraryImport("libc")]
    private static partial nint getpwuid(uint uid);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc")]
    private static partial void free(nint pointer);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int pipe2(int* fds, int flags);

    // fcntl is variadic in C; its third argument is an int for F_SETFD, which every Linux ABI passes like a fixed one.
    [LibraryImport("libc", SetLastError = true)]
    public static partial int fcntl(int fd, int command, int argument);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    // open is variadic in C; its mode argument is passed like a fixed one on every Linux ABI .NET runs on.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags, uint mode);

    [LibraryImport("libc")]
    public static partial int getppid();

    [LibraryImport("libc", SetLastError = true)]
    public static partial int kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint count, int timeout);

    // syscall(2) is variadic in C but implemented in assembly that only moves registers, so a fixed signature is safe.
    // Unused trailing arguments are passed as 0: the kernel requires zero flags.
    [LibraryImport("libc", SetLastError = true)]
    public static partial long syscall(long number, nint a1, nint a2, nint a3, nint a4);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int execve(string path, nint* argv, nint* envp);
}
