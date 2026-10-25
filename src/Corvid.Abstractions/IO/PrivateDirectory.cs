namespace Corvid.Abstractions.IO;

/// <summary>
/// The data directory holds the event log, memory, the egress store, the gateway's pairings and the jobs, which nobody else
/// on the machine should list or read: whichever store opens first on a fresh home creates it owner only (0700) here
/// (A4 verification), not with the umask.
/// </summary>
public static class PrivateDirectory
{
    /// <summary>
    /// Creates <paramref name="directory"/> owner only (0700) when it does not exist yet. Its parents are created as usual,
    /// and an existing directory keeps the mode its owner gave it.
    /// </summary>
    public static void Create(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (Directory.Exists(directory))
            return;
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))) is { } parent)
            Directory.CreateDirectory(parent);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Creates the directory <paramref name="file"/> goes in, owner only when it does not exist yet (<see cref="Create"/>).</summary>
    public static void CreateFor(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        Create(Path.GetDirectoryName(Path.GetFullPath(file))!);
    }
}
