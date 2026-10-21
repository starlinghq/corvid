using System.Security.Cryptography;
using System.Text;

namespace Corvid.Memory;

/// <summary>Where a project id came from (C-025): the origin remote, the main checkout's path, or the workspace path.</summary>
public static class ProjectSources
{
    public const string Remote = "remote";
    public const string Checkout = "checkout";
    public const string Path = "path";
}

/// <summary>A resolved project: its id and which key it was hashed from (<see cref="ProjectSources"/>).</summary>
public sealed record ProjectIdentity(string Id, string Source);

/// <summary>
/// Project ids (C-025): the 64-character lowercase hex SHA-256 of a normalized, lowercased key, so lessons are scoped to
/// one project without storing its remote or path.
/// </summary>
public static class ProjectIds
{
    /// <summary>
    /// The id of a git remote. Scheme (https, http, ssh, git, git+ssh) and userinfo are dropped, so a credential in the URL
    /// never changes or enters the key; the scp form <c>git@host:org/repo</c> becomes <c>host/org/repo</c>; a trailing
    /// <c>/</c> and <c>.git</c> are stripped. The https and ssh remotes of one repository therefore share an id.
    /// </summary>
    public static string FromRemote(string url) => Hash(NormalizeRemote(url));

    /// <summary>
    /// The id of a remote read from the repository whose main checkout is <paramref name="checkout"/>. A relative local path
    /// (no scheme, not scp form, not rooted: <c>../upstream.git</c>) is resolved against the checkout first, so unrelated
    /// repositories with the same relative origin do not share an id or each other's lessons.
    /// </summary>
    public static string FromRemote(string url, string checkout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkout);
        return FromRemote(IsRelativePath(url) ? System.IO.Path.GetFullPath(url.Trim(), checkout) : url);
    }

    internal static bool IsRelativePath(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var text = url.Trim();
        if (text.Contains("://", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(text))
            return false;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        return !(colon > 0 && (slash < 0 || colon < slash));
    }

    /// <summary>The id of a directory: its full path with <c>/</c> separators and no trailing separator.</summary>
    public static string FromPath(string path) => Hash(NormalizePath(path));

    /// <summary>The longest project argument <see cref="Candidates"/> considers.</summary>
    public const int MaxArgumentLength = 4_096;

    /// <summary>
    /// The project ids a caller's project argument may stand for (C-028), most specific first, by string work alone: no git
    /// and no file system. A 64-hex id is itself; a URL or scp-form remote is <see cref="FromRemote(string)"/>; an absolute
    /// path, normalized lexically (so <c>..</c> never reaches the disk and a path that does not exist still maps), is the
    /// path's id and each ancestor's, stopping below <paramref name="home"/> as discovery's <c>GIT_CEILING_DIRECTORIES</c>
    /// does (the home directory itself only on an exact match). A path equal to or under <paramref name="workspace"/> also
    /// stands for <paramref name="workspaceProject"/>, placed where the walk passes the workspace. Anything else (relative,
    /// with a NUL, longer than <see cref="MaxArgumentLength"/>) stands for nothing.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string project, string? home, string? workspace = null, string? workspaceProject = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var text = project.Trim();
        if (text.Length == 0 || text.Length > MaxArgumentLength || text.Contains('\0', StringComparison.Ordinal))
            return [];
        if (IsValid(text.ToLowerInvariant()))
            return [text.ToLowerInvariant()];
        if (text.Contains("://", StringComparison.Ordinal) || (!System.IO.Path.IsPathRooted(text) && !IsRelativePath(text)))
            return [FromRemote(text)];
        if (!System.IO.Path.IsPathFullyQualified(text))
            return [];
        var full = Lexical(text);
        var homeRoot = home is { Length: > 0 } && System.IO.Path.IsPathFullyQualified(home) ? Lexical(home) : null;
        var workspaceRoot = workspaceProject is not null && workspace is { Length: > 0 } && System.IO.Path.IsPathFullyQualified(workspace)
            ? Lexical(workspace) : null;
        var ceiling = homeRoot is not null && full.StartsWith(homeRoot.TrimEnd('/') + "/", StringComparison.Ordinal) ? homeRoot : null;
        var ids = new List<string>();
        var workspaceAdded = false;
        for (var current = full; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if (current == ceiling)
                break;
            ids.Add(FromPath(current));
            if (current == workspaceRoot)
            {
                ids.Add(workspaceProject!);
                workspaceAdded = true;
            }
            if (current == homeRoot)
                break;
        }
        if (!workspaceAdded && workspaceRoot is not null
            && (full == workspaceRoot || full.StartsWith(workspaceRoot.TrimEnd('/') + "/", StringComparison.Ordinal)))
            ids.Add(workspaceProject!);
        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A full path without a trailing separator. On Unix <c>GetFullPath</c> is lexical: it never touches the disk.</summary>
    private static string Lexical(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)).Replace('\\', '/');

    internal static string NormalizeRemote(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var text = url.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        var scp = false;
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }
        else
        {
            // scp form: [user@]host:path, where the colon comes before any slash.
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            scp = colon > 0 && (slash < 0 || colon < slash);
        }
        // Userinfo ends at the last '@' before the path, so a password containing '@' is dropped whole.
        var pathStart = scp ? text.IndexOf(':', StringComparison.Ordinal) : text.IndexOf('/', StringComparison.Ordinal);
        var authority = pathStart < 0 ? text : text[..pathStart];
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            text = text[(at + 1)..];
            if (pathStart >= 0)
                pathStart -= at + 1;
        }
        if (scp && pathStart > 0)
            text = text[..pathStart] + "/" + text[(pathStart + 1)..].TrimStart('/');
        else if (!scp && pathStart > 0)
        {
            // An explicit port in an ssh URL (ssh://host:22/org/repo) is not part of the repository's name.
            var host = text[..pathStart];
            var port = host.LastIndexOf(':');
            if (port > 0 && host[(port + 1)..].All(char.IsAsciiDigit))
                text = host[..port] + text[pathStart..];
        }
        text = text.TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];
        return text.TrimEnd('/').ToLowerInvariant();
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path).Replace('\\', '/');
        var trimmed = full.TrimEnd('/');
        return (trimmed.Length == 0 ? "/" : trimmed).ToLowerInvariant();
    }

    internal static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())));

    /// <summary>True for a 64-character lowercase hex id.</summary>
    public static bool IsValid(string? id) => id is { Length: 64 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}
