using System.Runtime.InteropServices;

namespace Corvid.Tools.Builtin;

/// <summary>
/// What kind of file a path names, links followed. .NET reports a FIFO, a socket or a device as a file, and opening one to
/// read can block for ever (a FIFO with no writer) or never reach the end (<c>/dev/zero</c>), so the file tools open only
/// regular files.
/// </summary>
internal static partial class FileKinds
{
    private const int AtFdCwd = -100;
    private const uint StatxType = 0x1;

    /// <summary>
    /// What <paramref name="path"/> is when it exists and is not a regular file ("a named pipe (FIFO)", "a socket", ...);
    /// null for a regular file, and when the kind cannot be told (a path that does not exist, or a platform without
    /// <c>statx</c>), so the caller's own checks decide.
    /// </summary>
    public static string? Special(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsLinux())
            return null;
        Statx status;
        try
        {
            // statx's layout is the same on every architecture, unlike struct stat's.
            if (statx(AtFdCwd, path, 0, StatxType, out status) != 0 || (status.Mask & StatxType) == 0)
                return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        return (status.Mode & 0xF000) switch
        {
            0x8000 or 0x4000 => null,
            0x1000 => "a named pipe (FIFO)",
            0x2000 => "a character device",
            0x6000 => "a block device",
            0xC000 => "a socket",
            _ => "a special file",
        };
    }

    // struct statx (linux/stat.h): only the leading fields are read; Size covers the whole 256-byte structure.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct Statx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint Links;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int directory, string path, int flags, uint mask, out Statx status);
}
