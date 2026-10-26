namespace Corvid.App.Engine.Sessions;

/// <summary>
/// A6 (G-4): a session's workspace in words, for the brand line and the status bar: the folder's name, and the branch its
/// checkout is on, read from the repository's <c>HEAD</c> file (no git runs). Both are text from the disk, so the views
/// clean them (<c>SafeText.Clean</c>) as they clean everything else the engine says.
/// </summary>
internal static class WorkspaceText
{
    // A HEAD file is one line; anything longer is not one to show.
    private const int MaxHead = 512;

    /// <summary>The folder's name ("corvid"), or the path itself for the file system's root.</summary>
    public static string Name(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    /// <summary>
    /// The branch the checkout holding <paramref name="path"/> is on ("main"), the commit's first seven characters when
    /// it is detached, or null outside a repository or when its <c>HEAD</c> cannot be read.
    /// </summary>
    public static string? Branch(string path)
    {
        try
        {
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            {
                var dotGit = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(dotGit))
                    return FromHead(Path.Combine(dotGit, "HEAD"));
                if (File.Exists(dotGit))
                {
                    // A linked worktree: ".git" names its git directory ("gitdir: /repo/.git/worktrees/name").
                    var line = ReadLine(dotGit);
                    if (line is null || !line.StartsWith("gitdir:", StringComparison.Ordinal))
                        return null;
                    var gitDirectory = line["gitdir:".Length..].Trim();
                    if (!Path.IsPathFullyQualified(gitDirectory))
                        gitDirectory = Path.GetFullPath(Path.Combine(directory.FullName, gitDirectory));
                    return FromHead(Path.Combine(gitDirectory, "HEAD"));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable checkout has no branch to show.
        }
        return null;
    }

    private static string? FromHead(string head)
    {
        var line = ReadLine(head);
        if (line is null)
            return null;
        const string Ref = "ref: refs/heads/";
        if (line.StartsWith(Ref, StringComparison.Ordinal))
            return line[Ref.Length..].Trim() is { Length: > 0 } branch ? branch : null;
        return line.Length >= 7 && line.All(Uri.IsHexDigit) ? line[..7] : null;
    }

    private static string? ReadLine(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > MaxHead)
            return null;
        return File.ReadAllText(file).Split('\n', 2)[0].Trim();
    }
}
