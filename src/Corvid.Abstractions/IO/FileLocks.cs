namespace Corvid.Abstractions.IO;

/// <summary>
/// Telling a lock another process holds from a lock file that cannot be opened at all. Corvid's cross-process locks
/// (<c>.corvid.json.lock</c>, <c>approvals.json.lock</c>) are a lock file opened with no sharing: an exclusive
/// <c>flock</c> on Unix, a share-none open on Windows. Only a refusal for that reason is worth waiting on; a read-only
/// directory or file system refuses at once and for good, and says so rather than reading as another process writing.
/// </summary>
public static class FileLocks
{
    // Linux's EAGAIN (EWOULDBLOCK), which .NET reports as the IOException's HResult when flock finds the file locked;
    // macOS and the BSDs number it 35. Windows reports ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION as an HRESULT.
    private const int LinuxWouldBlock = 11;
    private const int BsdWouldBlock = 35;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    /// <summary>The open failed because another handle holds the file's lock: waiting may help.</summary>
    public static bool IsHeldElsewhere(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (OperatingSystem.IsWindows())
            return (exception.HResult & 0xFFFF) is SharingViolation or LockViolation;
        return exception.HResult == (OperatingSystem.IsLinux() ? LinuxWouldBlock : BsdWouldBlock);
    }

    /// <summary>
    /// Why the lock file could not be opened, other than another process holding it, in a clause: "the directory
    /// /x is read-only to Corvid" for a directory or file system it may not write, the system's words otherwise.
    /// </summary>
    public static string Unopenable(string lockPath, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        ArgumentNullException.ThrowIfNull(exception);
        var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath)) ?? lockPath;
        return exception is UnauthorizedAccessException || ReadOnly(directory)
            ? $"the directory {directory} is read-only to Corvid ({exception.Message})"
            : $"{lockPath} cannot be opened ({exception.Message})";
    }

    private static bool ReadOnly(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return false;
            if (OperatingSystem.IsWindows())
                return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReadOnly);
            // Without the owner's write bit (the common case); a read-only mount shows in the failed open's own message.
            return !File.GetUnixFileMode(directory).HasFlag(UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
