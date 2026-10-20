using System.Collections.Immutable;
using Corvid.Approvals;

namespace Corvid.Tools.Builtin;

/// <summary>
/// The directory the file tools may touch. A path from the model is resolved against it and must stay inside it after
/// every symbolic link is followed, so <c>link -> /etc</c> inside the workspace does not lead out. Paths at or under a
/// denied path (Vault's directories, credential stores, Corvid's safety configuration, when the workspace contains them)
/// and credential file names (<c>.env</c>, private keys) are refused, matching what the sandbox hides from the shell.
/// </summary>
/// <remarks>
/// The check and the open are separate calls, so a concurrent process that swaps a directory for a link in between can
/// still redirect one access; the shell, the only Corvid tool that makes links, runs one call at a time.
/// </remarks>
public sealed class Workspace
{
    private const int MaxLinks = 40;

    /// <param name="root">An existing directory. It is canonicalized, links included.</param>
    /// <param name="deniedPaths">Absolute paths the tools must not reach even inside the workspace.</param>
    public Workspace(string root, IEnumerable<string>? deniedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("The workspace must be an absolute path.", nameof(root));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The workspace {root} does not exist.");
        Root = RealPath(Path.GetFullPath(root));
        Denied = [.. (deniedPaths ?? []).Where(path => Path.IsPathFullyQualified(path))
            .SelectMany(path => new[] { Normalize(path), RealPath(Normalize(path)) }).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The workspace's canonical path.</summary>
    public string Root { get; }

    public ImmutableArray<string> Denied { get; }

    /// <summary>
    /// The workspace for an approval environment: denied paths are its protected Vault directories, credential stores and
    /// safety configuration, which the host extends with Vault's configured locations (<see cref="VaultLocations.Protect"/>).
    /// </summary>
    public static Workspace For(ApprovalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var protectedPaths = environment.Protected;
        return new Workspace(environment.Workspace,
            protectedPaths.Vault.Concat(protectedPaths.CredentialStores).Concat(protectedPaths.SafetyConfiguration));
    }

    /// <summary>
    /// Resolves <paramref name="path"/> (relative to the workspace, or absolute inside it) to a canonical absolute path
    /// inside the workspace. Components that do not exist yet are kept as written.
    /// </summary>
    /// <exception cref="ToolArgumentException">The path leaves the workspace, is denied, or names a credential file.</exception>
    public string Resolve(string path, string parameter = "path")
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || path.Contains('\0'))
            throw new ToolArgumentException($"Argument '{parameter}' must be a path in the workspace.", parameter);
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            throw new ToolArgumentException($"Argument '{parameter}' must be inside the workspace; '~' is outside it. Use a path relative to the workspace.", parameter);
        var lexical = Normalize(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        if (!AtOrUnder(lexical, Root))
            throw Outside(path, parameter);
        string real;
        try
        {
            real = RealPath(lexical);
        }
        catch (IOException exception)
        {
            throw new ToolArgumentException($"Argument '{parameter}': {exception.Message}", parameter);
        }

        if (!AtOrUnder(real, Root))
            throw new ToolArgumentException($"Argument '{parameter}' is '{path}', which leads outside the workspace through a symbolic link.", parameter);
        if (Denied.FirstOrDefault(denied => AtOrUnder(real, denied) || AtOrUnder(lexical, denied)) is { } protectedPath)
            throw new ToolArgumentException($"Argument '{parameter}' is '{path}', which is inside a protected location ({protectedPath}).", parameter);
        if (Path.GetFileName(real) is { Length: > 0 } name && IsSecretName(name))
            throw new ToolArgumentException($"Argument '{parameter}' is '{path}', a credential file; the tools do not read or write those.", parameter);
        return real;
    }

    /// <summary>The path relative to the workspace, as the tools report it (<c>.</c> for the root).</summary>
    public string Relative(string absolute)
    {
        var relative = Path.GetRelativePath(Root, absolute);
        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>
    /// Names the tools never show or open: the sandbox's <c>.env</c> rule (examples included, as the sandbox hides them)
    /// and the approval pipeline's credential names (private keys and certificates).
    /// </summary>
    public static bool IsSecretName(string name) =>
        Corvid.Sandbox.BuiltInDenials.IsEnvFileName(name) || ProtectedPaths.IsCredentialFileName(name);

    /// <summary>True when an entry found while walking the workspace may be shown: not a credential file, not denied.</summary>
    internal bool Visible(string absolute) =>
        !IsSecretName(Path.GetFileName(absolute)) && !Denied.Any(denied => AtOrUnder(absolute, denied));

    private ToolArgumentException Outside(string path, string parameter) =>
        new($"Argument '{parameter}' is '{path}', which is outside the workspace ({Root}). Use a path relative to the workspace.", parameter);

    internal static bool AtOrUnder(string path, string directory) =>
        path == directory || (directory == "/" ? path.StartsWith('/') : path.StartsWith(directory + "/", StringComparison.Ordinal));

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    /// <summary>
    /// Follows symbolic links in every existing component, like realpath(3), keeping missing components as written. Throws
    /// <see cref="IOException"/> on a link loop.
    /// </summary>
    internal static string RealPath(string absolute)
    {
        var pending = new Stack<string>(absolute.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var links = 0;
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            if (part == ".")
                continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = current == "/" ? "/" + part : current + "/" + part;
            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (UnauthorizedAccessException)
            {
                target = null;
            }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++links > MaxLinks)
                throw new IOException($"Too many levels of symbolic links at {next}.");
            if (Path.IsPathRooted(target))
                current = "/";
            foreach (var component in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                pending.Push(component);
        }

        return current;
    }
}
