using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Corvid.Tools.Builtin;

/// <summary>
/// What kind of file a path names, links followed. .NET reports a FIFO, a socket or a device as a file, and opening one to
/// read can block for ever (a FIFO with no writer) or never reach the end (<c>/dev/zero</c>), so the file tools open only
/// regular files.
/// </summary>
internal static partial class FileKinds
{
    private const int AtFdCwd = -100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x1;

    // open(2) flags: the asm-generic values, which every architecture .NET runs Linux on uses.
    private const int OpenReadOnly = 0;
    private const int OpenNoCtty = 0x100;
    private const int OpenNonBlock = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;

    private const int Eperm = 1;
    private const int Enoent = 2;
    private const int Eacces = 13;
    private const int Eloop = 40;

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
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }

        return Kind(status.Mode, directory: true);
    }

    /// <summary>
    /// Reads the whole of <paramref name="path"/>, which must be a regular file. On Linux the kind is checked on the open
    /// descriptor, not on the path: a path checked with <see cref="Special"/> could be swapped for a FIFO before it is
    /// opened. The open never blocks (a FIFO with no writer opens at once, and is then refused) and does not follow a final
    /// link (the caller's path is already resolved, so a link there was made since). Elsewhere it is a plain read.
    /// </summary>
    /// <exception cref="SpecialFileException">The path is a FIFO, socket, device or directory.</exception>
    public static byte[] ReadRegular(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!OperatingSystem.IsLinux())
            return File.ReadAllBytes(path);
        int descriptor;
        try
        {
            descriptor = open(path, OpenReadOnly | OpenNonBlock | OpenNoCtty | OpenNoFollow | OpenCloseOnExec);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return File.ReadAllBytes(path);
        }

        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            var message = $"'{path}': {Marshal.GetPInvokeErrorMessage(error)}";
            throw error switch
            {
                Enoent => new FileNotFoundException(message, path),
                Eacces or Eperm => new UnauthorizedAccessException(message),
                Eloop => new IOException($"'{path}' became a link while it was being read."),
                // A socket cannot be opened at all (ENXIO): say what it is.
                _ => Special(path) is { } kind ? new SpecialFileException(kind) : new IOException(message),
            };
        }

        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        if (statx(descriptor, "", AtEmptyPath, StatxType, out var status) != 0 || (status.Mask & StatxType) == 0)
            throw new IOException($"'{path}': its kind cannot be told ({Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}).");
        if (Kind(status.Mode, directory: false) is { } special)
            throw new SpecialFileException(special);

        // pread through the handle (O_NONBLOCK means nothing for a regular file). A file that grows meanwhile is read as far
        // as its length when opened; one that shrinks, as far as it goes.
        var bytes = new byte[RandomAccess.GetLength(handle)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = RandomAccess.Read(handle, bytes.AsSpan(read), read);
            if (count == 0)
                break;
            read += count;
        }
        return read == bytes.Length ? bytes : bytes[..read];
    }

    private static string? Kind(ushort mode, bool directory) => (mode & 0xF000) switch
    {
        0x8000 => null,
        0x4000 => directory ? null : "a directory",
        0x1000 => "a named pipe (FIFO)",
        0x2000 => "a character device",
        0x6000 => "a block device",
        0xC000 => "a socket",
        _ => "a special file",
    };

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

    // open is variadic in C; without O_CREAT no mode is passed, and the fixed arguments are passed alike on Linux's ABIs.
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);
}

/// <summary>A file tool was asked to read something that is not a regular file; <see cref="Kind"/> says what it is.</summary>
internal sealed class SpecialFileException(string kind) : IOException($"It is {kind}, not a regular file.")
{
    public string Kind { get; } = kind;
}
