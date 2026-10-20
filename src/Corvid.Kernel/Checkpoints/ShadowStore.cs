using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Corvid.Approvals;
using Corvid.Sandbox;

namespace Corvid.Kernel.Checkpoints;

/// <summary>Where the shadow store lives, which git it runs and what it leaves out.</summary>
public sealed record CheckpointOptions
{
    /// <summary>The workspace checkpoints capture. Absolute.</summary>
    public required string Workspace { get; init; }

    /// <summary>The directory that holds one shadow store per workspace, under Corvid's data directory. Absolute.</summary>
    public required string StoreRoot { get; init; }

    /// <summary>The git executable: a path, or a name looked up on <c>PATH</c>.</summary>
    public string GitPath { get; init; } = "git";

    /// <summary>
    /// Absolute paths never copied into the store or written by a restore: Vault's directories, credential stores and
    /// Corvid's own state when the workspace contains them. The store's own directory is always added.
    /// </summary>
    public ImmutableArray<string> ExcludedPaths { get; init; } = [];

    /// <summary>File names never copied into the store or written by a restore (<c>.env</c>, private keys).</summary>
    public Func<string, bool>? IsExcludedName { get; init; }

    /// <summary>Larger files are left out of checkpoints, so changing one is not reversible.</summary>
    public long MaxFileBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>A checkpoint that would add or update more files than this fails instead (a first snapshot of a huge tree).</summary>
    public int MaxFiles { get; init; } = 50_000;

    /// <summary>A git command that runs longer is killed and the checkpoint fails.</summary>
    public TimeSpan GitTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The exclusions an approval environment implies: the paths the hardline list and the file tools protect, and the
    /// credential names the sandbox and the file tools hide, so the store never holds what Corvid may not read.
    /// </summary>
    public static CheckpointOptions For(ApprovalEnvironment environment, string storeRoot, string gitPath = "git")
    {
        ArgumentNullException.ThrowIfNull(environment);
        var protectedPaths = environment.Protected;
        return new CheckpointOptions
        {
            Workspace = environment.Workspace,
            StoreRoot = storeRoot,
            GitPath = gitPath,
            ExcludedPaths = [.. protectedPaths.Vault.Concat(protectedPaths.CredentialStores).Concat(protectedPaths.SafetyConfiguration)],
            IsExcludedName = name => BuiltInDenials.IsEnvFileName(name) || ProtectedPaths.IsCredentialFileName(name),
        };
    }
}

/// <summary>A file in a checkpoint: git's mode (<c>100644</c>, <c>100755</c>, <c>120000</c> for a link) and blob id.</summary>
public readonly record struct StoredFile(string Mode, string Blob)
{
    public bool IsLink => Mode == "120000";

    public bool IsExecutable => Mode == "100755";
}

/// <summary>
/// The shadow git store for one workspace (plan §15): a repository under Corvid's data directory whose work tree is the
/// workspace, so the user's own <c>.git</c> is never read or written. Snapshots are commits on one chain; the workspace's
/// <c>.gitignore</c> files are honoured, and excluded paths (secrets, Corvid's state, oversized files, nested
/// repositories) are never added. Files are stored byte for byte: the store's attributes turn off every conversion and
/// filter the workspace's <c>.gitattributes</c> could ask for.
/// </summary>
/// <remarks>Operations on one store are serialized in this process; a second process using it fails on git's index lock.</remarks>
public sealed class ShadowStore
{
    private const string Branch = "refs/heads/checkpoints";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    private readonly CheckpointOptions _options;
    private readonly string _configuredWorkspace;
    private readonly ImmutableArray<string> _excluded;
    private readonly SemaphoreSlim _lock;
    private readonly string? _git;
    private readonly string? _unavailable;
    private GitRunner? _runner;

    public ShadowStore(CheckpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.Workspace) || !Path.IsPathFullyQualified(options.StoreRoot))
            throw new ArgumentException("The workspace and the store root must be absolute paths.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxFiles);
        _options = options;
        _configuredWorkspace = Normalize(options.Workspace);
        Workspace = RealPath(_configuredWorkspace);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Workspace)))[..16];
        Directory = Path.Combine(Normalize(options.StoreRoot), key);
        _excluded = [.. options.ExcludedPaths.Append(Normalize(options.StoreRoot)).Where(Path.IsPathFullyQualified)
            .SelectMany(path => new[] { Normalize(path), RealPath(Normalize(path)) }).Distinct(StringComparer.Ordinal)];
        _lock = Locks.GetOrAdd(Directory, _ => new SemaphoreSlim(1, 1));

        _git = GitRunner.Resolve(options.GitPath, Environment.GetEnvironmentVariable("PATH"));
        if (!System.IO.Directory.Exists(Workspace))
            _unavailable = $"the workspace {Workspace} does not exist";
        else if (_git is null)
            _unavailable = options.GitPath.Contains('/')
                ? $"git was not found at {options.GitPath}"
                : "git was not found on PATH";
        else if (AtOrUnder(Directory, Workspace) && !_excluded.Any(path => AtOrUnder(Directory, path)))
            _unavailable = "the store would be inside the workspace";
    }

    /// <summary>The workspace's canonical path.</summary>
    public string Workspace { get; }

    /// <summary>This workspace's store: the git directory and scratch space, created with owner-only access.</summary>
    public string Directory { get; }

    /// <summary>Null when checkpoints can be taken; otherwise why not, for messages and <c>corvid doctor</c>.</summary>
    public string? UnavailableReason => _unavailable;

    public bool IsAvailable => _unavailable is null;

    public long MaxFileBytes => _options.MaxFileBytes;

    /// <summary>
    /// Snapshots the workspace as a commit and returns its id. <paramref name="include"/> names paths (relative to the
    /// workspace) to capture even when a <c>.gitignore</c> leaves them out, such as a file a tool is about to write.
    /// </summary>
    public async Task<string> SnapshotAsync(string message, IEnumerable<string>? include = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var git = await ReadyAsync().ConfigureAwait(false);
            // Only what changed since the last snapshot: modified and deleted files the index tracks, and untracked files
            // the workspace does not ignore. The index's stat data makes this cheap after the first snapshot.
            var listed = await git.RunAsync(["ls-files", "-z", "--modified", "--deleted", "--others", "--exclude-standard"]).ConfigureAwait(false);
            var candidates = new SortedSet<string>(Split(listed.Output), StringComparer.Ordinal);
            foreach (var path in include ?? [])
                if (Relative(path) is { } relative && File.Exists(Path.Combine(Workspace, relative)))
                    candidates.Add(relative);

            var add = new List<string>();
            var remove = new List<string>();
            foreach (var path in candidates)
            {
                // A nested repository shows as its directory; git would record a link, not the files, so it is left out.
                if (path.EndsWith('/'))
                    continue;
                var full = Path.Combine(Workspace, path);
                var info = new FileInfo(full);
                if (Excluded(path) || System.IO.Directory.Exists(full) && info.LinkTarget is null
                    || info.Exists && info.LinkTarget is null && info.Length > _options.MaxFileBytes)
                    remove.Add(path);
                else
                    add.Add(path);
            }
            if (add.Count > _options.MaxFiles)
                throw new CheckpointException($"{add.Count} files changed since the last checkpoint, over the limit of {_options.MaxFiles}; " +
                    "add a .gitignore for generated files, raise Corvid:Checkpoints:MaxFiles, or turn checkpoints off.");
            if (add.Count > 0)
                await git.RunAsync(["update-index", "--add", "--remove", "-z", "--stdin"], Join(add)).ConfigureAwait(false);
            if (remove.Count > 0)
                await git.RunAsync(["update-index", "--force-remove", "-z", "--stdin"], Join(remove)).ConfigureAwait(false);

            var tree = (await git.RunAsync(["write-tree"]).ConfigureAwait(false)).Text.Trim();
            var parent = (await git.RunAsync(["rev-parse", "-q", "--verify", Branch + "^{commit}"], accept: [0, 1]).ConfigureAwait(false)).Text.Trim();
            List<string> commit = ["commit-tree", tree, "-m", message];
            if (parent.Length > 0)
                commit.InsertRange(2, ["-p", parent]);
            var id = (await git.RunAsync(commit).ConfigureAwait(false)).Text.Trim();
            await git.RunAsync(["update-ref", Branch, id]).ConfigureAwait(false);
            return id;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The files in a checkpoint, by path relative to the workspace: all of them, or those at or under <paramref name="paths"/>.</summary>
    public async Task<ImmutableDictionary<string, StoredFile>> ReadAsync(string commit, IEnumerable<string>? paths = null)
    {
        List<string> arguments = ["ls-tree", "-r", "-z", "--full-tree", Revision(commit)];
        if (paths is not null)
        {
            var relative = paths.Select(Relative).OfType<string>().ToList();
            if (relative.Count == 0)
                return ImmutableDictionary<string, StoredFile>.Empty;
            arguments.Add("--");
            // Literal: a file called '*.txt' means that file, not a pattern.
            arguments.AddRange(relative.Select(path => ":(literal)" + path));
        }
        var listed = await LockedAsync(git => git.RunAsync(arguments)).ConfigureAwait(false);
        var files = ImmutableDictionary.CreateBuilder<string, StoredFile>(StringComparer.Ordinal);
        foreach (var line in Split(listed.Output))
        {
            // "<mode> <type> <blob>\t<path>"
            var tab = line.IndexOf('\t');
            var fields = line[..tab].Split(' ');
            if (fields is [var mode, "blob", var blob])
                files[line[(tab + 1)..]] = new StoredFile(mode, blob);
        }
        return files.ToImmutable();
    }

    /// <summary>The paths whose content or mode differ between two checkpoints.</summary>
    public async Task<ImmutableArray<string>> ChangedAsync(string from, string to)
    {
        var listed = await LockedAsync(git => git.RunAsync(["diff-tree", "-r", "-z", "--no-renames", "--name-only", Revision(from), Revision(to)]))
            .ConfigureAwait(false);
        return [.. Split(listed.Output).Order(StringComparer.Ordinal)];
    }

    public async Task<bool> ContainsAsync(string commit)
    {
        if (!IsRevision(commit))
            return false;
        var result = await LockedAsync(git => git.RunRawAsync(["cat-file", "-e", commit + "^{commit}"])).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<byte[]> ReadBlobAsync(string blob) =>
        (await LockedAsync(git => git.RunAsync(["cat-file", "blob", Revision(blob)])).ConfigureAwait(false)).Output;

    public async Task<string> WriteBlobAsync(byte[] content) =>
        (await LockedAsync(git => git.RunAsync(["hash-object", "-w", "--no-filters", "--stdin"], content)).ConfigureAwait(false)).Text.Trim();

    /// <summary>
    /// A three-way merge that takes the change from <paramref name="base"/> to <paramref name="other"/> into
    /// <paramref name="current"/>. Null when it conflicts or a side is binary.
    /// </summary>
    public async Task<byte[]?> MergeAsync(byte[] current, byte[] @base, byte[] other)
    {
        var scratch = Path.Combine(Directory, "tmp", Guid.NewGuid().ToString("N"));
        CreatePrivate(scratch);
        try
        {
            string[] files = [Path.Combine(scratch, "current"), Path.Combine(scratch, "base"), Path.Combine(scratch, "other")];
            await File.WriteAllBytesAsync(files[0], current).ConfigureAwait(false);
            await File.WriteAllBytesAsync(files[1], @base).ConfigureAwait(false);
            await File.WriteAllBytesAsync(files[2], other).ConfigureAwait(false);
            var result = await LockedAsync(git => git.RunRawAsync(["merge-file", "-p", "-q", .. files])).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.Output : null;
        }
        finally
        {
            System.IO.Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Whether the workspace's ignore rules leave a path out of snapshots (a path relative to the workspace).</summary>
    public async Task<bool> IsIgnoredAsync(string relative)
    {
        var result = await LockedAsync(git => git.RunRawAsync(["check-ignore", "-q", "--no-index", "--", relative])).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => true,
            1 => false,
            _ => throw new CheckpointException($"git check-ignore failed: {result.Error.Trim()}"),
        };
    }

    /// <summary>
    /// The path relative to the workspace when it is inside it (lexically), else null. The workspace may be named as
    /// configured or by its real path: the approval pipeline resolves targets against the former, which can be a link.
    /// </summary>
    public string? Relative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Normalize(Path.IsPathRooted(path) ? path : Path.Combine(Workspace, path));
        foreach (var root in new[] { Workspace, _configuredWorkspace })
            if (full != root && AtOrUnder(full, root))
                return full[(root.Length + 1)..];
        return null;
    }

    /// <summary>Whether the path is the workspace directory itself, by its configured or real name.</summary>
    public bool IsWorkspace(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Normalize(path);
        return full == Workspace || full == _configuredWorkspace;
    }

    /// <summary>
    /// Paths a checkpoint never holds and a restore never writes: a <c>.git</c> component (the user's repository, or a
    /// nested one), an excluded name, or anything at or under an excluded path.
    /// </summary>
    public bool Excluded(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var parts = relative.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (_options.IsExcludedName?.Invoke(parts[^1]) == true)
            return true;
        var full = Workspace + "/" + relative;
        return _excluded.Any(path => AtOrUnder(full, path));
    }

    private async Task<T> LockedAsync<T>(Func<GitRunner, Task<T>> action)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await action(await ReadyAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates the store on first use; refuses (fails closed) when git is missing or the store belongs to another workspace.</summary>
    private async Task<GitRunner> ReadyAsync()
    {
        if (_unavailable is not null)
            throw new CheckpointException("Checkpoints are unavailable: " + _unavailable + ".");
        if (_runner is not null)
            return _runner;
        // Owner-only: the store holds copies of the workspace's files.
        CreatePrivate(_options.StoreRoot);
        CreatePrivate(Directory);
        var home = CreatePrivate(Path.Combine(Directory, "home"));
        var gitDirectory = Path.Combine(Directory, "git");
        var owner = Path.Combine(Directory, "workspace");
        if (File.Exists(owner) && (await File.ReadAllTextAsync(owner).ConfigureAwait(false)).Trim() != Workspace)
            throw new CheckpointException($"The checkpoint store {Directory} belongs to another workspace.");

        var runner = new GitRunner(_git!, gitDirectory, Workspace, home, _options.GitTimeout);
        if (!File.Exists(Path.Combine(gitDirectory, "HEAD")))
        {
            // No templates: the store starts without sample hooks or a description file.
            await runner.RunAsync(["init", "-q", "--template="]).ConfigureAwait(false);
            await File.WriteAllTextAsync(owner, Workspace + "\n").ConfigureAwait(false);
        }
        // Rewritten on every start, so a changed exclusion list applies and nothing planted in the store survives.
        var info = System.IO.Directory.CreateDirectory(Path.Combine(gitDirectory, "info")).FullName;
        await File.WriteAllTextAsync(Path.Combine(info, "attributes"),
            "# Corvid: store bytes as they are, whatever the workspace's .gitattributes ask for.\n" +
            "* -text -filter -ident -diff !eol !working-tree-encoding\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(info, "exclude"), ExcludeFile()).ConfigureAwait(false);
        _runner = runner;
        return runner;
    }

    /// <summary>Excluded directories inside the workspace, so listing never walks them; the code filter remains the rule.</summary>
    private string ExcludeFile()
    {
        var text = new StringBuilder("# Corvid: protected paths inside the workspace.\n");
        foreach (var path in _excluded)
            if (Relative(path) is { } relative)
                text.Append('/').Append(EscapePattern(relative)).Append('\n');
        return text.ToString();
    }

    private static string EscapePattern(string path)
    {
        var escaped = new StringBuilder();
        foreach (var c in path)
        {
            if (c is '\\' or '*' or '?' or '[' or ']' or '!' or '#' or ' ')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    private static string CreatePrivate(string path) => (OperatingSystem.IsWindows()
        ? System.IO.Directory.CreateDirectory(path)
        : System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)).FullName;

    private static string Revision(string id) =>
        IsRevision(id) ? id : throw new CheckpointException($"'{id}' is not a checkpoint revision.");

    private static bool IsRevision(string id) => id.Length is 40 or 64 && id.All(char.IsAsciiHexDigitLower);

    private static IEnumerable<string> Split(byte[] output) =>
        Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static byte[] Join(IEnumerable<string> paths) => Encoding.UTF8.GetBytes(string.Concat(paths.Select(path => path + "\0")));

    internal static bool AtOrUnder(string path, string directory) =>
        path == directory || (directory == "/" ? path.StartsWith('/') : path.StartsWith(directory + "/", StringComparison.Ordinal));

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }

    private static string RealPath(string path)
    {
        try
        {
            return Normalize(RealPaths.Resolve(path));
        }
        catch (IOException)
        {
            return path;
        }
    }
}
