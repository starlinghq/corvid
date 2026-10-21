using System.Net.Sockets;

namespace Corvid.Tests;

/// <summary>
/// A fresh temporary directory short enough to hold a Unix socket. A socket path is limited to 108 bytes (104 on macOS),
/// and a TMPDIR under a deep worktree can use most of that, so the first of TMPDIR, <c>$XDG_RUNTIME_DIR</c> and
/// <c>/tmp</c> that leaves room is used.
/// </summary>
internal static class ShortTemp
{
    // Room for the longest name a test puts under the directory ("cv-xxxxxxxxxxxx/run/vault.sock") within 104 bytes.
    private const int MaxBase = 70;

    /// <summary>Creates and returns <c>&lt;base&gt;/&lt;prefix&gt;&lt;random&gt;</c>; the caller removes it.</summary>
    public static string Create(string prefix)
    {
        var directory = Name(prefix);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Runs the rest of a stand-in's set-up in <paramref name="directory"/> (from <see cref="Create"/>). If it throws (a
    /// socket bind failing, say), the directory and whatever set-up put there are removed before the exception propagates,
    /// since the stand-in was never made and nobody else will remove them.
    /// </summary>
    public static T SetUp<T>(string directory, Func<T> setUp)
    {
        ArgumentNullException.ThrowIfNull(setUp);
        try
        {
            return setUp();
        }
        catch
        {
            Remove(directory);
            throw;
        }
    }

    /// <inheritdoc cref="SetUp{T}(string, Func{T})"/>
    public static void SetUp(string directory, Action setUp)
    {
        ArgumentNullException.ThrowIfNull(setUp);
        SetUp(directory, () =>
        {
            setUp();
            return true;
        });
    }

    /// <summary>
    /// A Unix socket listening at <paramref name="socketPath"/>, in <paramref name="directory"/>. If it cannot bind or
    /// listen, the socket is closed and the directory removed, as <see cref="SetUp{T}(string, Func{T})"/> does.
    /// </summary>
    public static Socket Listen(string directory, string socketPath, int backlog = 16) => SetUp(directory, () =>
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    });

    private static void Remove(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the set-up's own exception is the one to report.
        }
    }

    /// <summary>A short path that does not exist, for a socket nobody listens on.</summary>
    public static string Name(string prefix) => Path.Combine(Base(), prefix + Guid.NewGuid().ToString("N")[..12]);

    private static string Base()
    {
        string?[] candidates = [Path.GetTempPath(), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), "/tmp"];
        foreach (var candidate in candidates)
        {
            if (candidate is { Length: > 0 } && Path.IsPathRooted(candidate) && Directory.Exists(candidate)
                && Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) is { Length: <= MaxBase } full)
                return full;
        }
        return Path.GetTempPath();
    }
}
