using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Corvid.Kernel.Checkpoints;

namespace Corvid.Kernel.Workspaces;

/// <summary>A worktree could not be listed, created or removed; the message says why in a sentence.</summary>
public sealed class WorktreeException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>The git repository a workspace is in.</summary>
/// <param name="TopLevel">The top of the working tree the workspace is in (a linked worktree's own top when it is one).</param>
/// <param name="MainCheckout">The repository's main checkout, whose branch state Corvid never changes.</param>
/// <param name="Subdirectory">The workspace relative to <paramref name="TopLevel"/> ("" at the top), kept in a new worktree.</param>
public sealed record GitRepository(string TopLevel, string MainCheckout, string Subdirectory);

/// <summary>One worktree Corvid created.</summary>
/// <param name="Path">The worktree's top directory.</param>
/// <param name="Branch">Its branch (<c>corvid/&lt;name&gt;</c>), or null when it was left detached.</param>
/// <param name="Head">The commit it is at.</param>
public sealed record GitWorktree(string Name, string Path, string? Branch, string Head);

/// <summary>
/// A6 (G-4): git worktrees for sessions, under one directory of Corvid's (outside every workspace and outside the data
/// directory, which the sandbox hides). Each is made with a new branch <c>corvid/&lt;name&gt;</c> from the workspace's
/// <c>HEAD</c>, so the main checkout's branch, index and files are never touched; removing one keeps its branch, and git
/// refuses to remove one with changes or untracked files. git runs as for checkpoints (<see cref="GitRunner.ForDiscovery"/>):
/// a cleared environment, no system or global configuration, hooks, fsmonitor, submodule recursion and credential helpers
/// off. A repository whose configuration names a filter driver (which git would run while checking files out or reading a
/// worktree's status, unsandboxed), as read in the new worktree itself or in any file it includes, is refused, since a tool
/// in a workspace can write that configuration.
/// </summary>
public sealed partial class GitWorktrees
{
    /// <summary>The prefix of every branch Corvid makes for a worktree.</summary>
    public const string BranchPrefix = "corvid/";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly string? _git;
    private readonly TimeSpan _timeout;

    /// <param name="git">The git executable: a path, or a name looked up on <c>PATH</c>.</param>
    /// <param name="directory">Where worktrees go: one directory per repository below it.</param>
    public GitWorktrees(string git, string directory, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(git);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The worktrees directory must be an absolute path.", nameof(directory));
        _git = GitRunner.Resolve(git, Environment.GetEnvironmentVariable("PATH"));
        // Real, as every workspace path is (WorkspaceBound records them resolved), so a worktree's path and the paths git
        // lists compare equal when the directory is reached through a symbolic link.
        Directory = Path.TrimEndingDirectorySeparator(RealPaths.Resolve(Path.GetFullPath(directory)));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Where worktrees go.</summary>
    public string Directory { get; }

    /// <summary>Why a name cannot name a worktree, or null when it can: lower-case letters, digits, '.', '_' and '-', 1 to 40.</summary>
    public static string? CheckName(string name) =>
        Names().IsMatch(name ?? "") && !name!.EndsWith(".lock", StringComparison.Ordinal) && !name.Contains("..", StringComparison.Ordinal)
            ? null
            : "A worktree's name is 1 to 40 lower-case letters, digits, '.', '_' or '-', starting with a letter or digit.";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    private static partial Regex Names();

    /// <summary>A name for a new worktree from the time and four random hex digits: <c>session-20261025-142233-3f9a</c>.</summary>
    public static string NewName(DateTimeOffset now) =>
        "session-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-"
        + RandomNumberGenerator.GetHexString(4, lowercase: true);

    /// <summary>The repository the workspace is in.</summary>
    /// <exception cref="WorktreeException">No git, not a repository (or a bare one), or one git refuses.</exception>
    public async Task<GitRepository> ResolveAsync(string workspace)
    {
        var runner = Runner(workspace);
        var found = await Run(runner, ["rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir"],
            $"{workspace} is not in a git repository Corvid can use").ConfigureAwait(false);
        var lines = found.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 2 || !Path.IsPathFullyQualified(lines[0]) || !Path.IsPathFullyQualified(lines[1]))
            throw new WorktreeException($"{workspace} is not in a git repository with a working tree.");
        var top = Path.TrimEndingDirectorySeparator(lines[0]);
        var common = Path.TrimEndingDirectorySeparator(lines[1]);
        if (Path.GetFileName(common) != ".git")
            throw new WorktreeException($"The repository of {workspace} keeps its git directory at {common}, not in a main checkout; Corvid makes worktrees only of a checkout.");
        var real = RealPaths.Resolve(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)));
        var subdirectory = real == top ? "" : real.StartsWith(top + "/", StringComparison.Ordinal) ? real[(top.Length + 1)..] : "";
        return new GitRepository(top, Path.GetDirectoryName(common)!, subdirectory);
    }

    /// <summary>The directory of one repository's worktrees: its main checkout's name and a short hash of its path.</summary>
    public string DirectoryOf(GitRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(repository.MainCheckout)))[..8];
        var name = Path.GetFileName(repository.MainCheckout);
        var safe = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')]);
        return Path.Combine(Directory, $"{(safe.Length == 0 ? "repo" : safe)}-{hash}");
    }

    /// <summary>The path a worktree of that name would have.</summary>
    public string PathOf(GitRepository repository, string name) => Path.Combine(DirectoryOf(repository), name);

    /// <summary>Corvid's worktrees of the repository (those under <see cref="DirectoryOf"/>), by name.</summary>
    public async Task<ImmutableArray<GitWorktree>> ListAsync(GitRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var listed = await Run(Runner(repository.MainCheckout), ["worktree", "list", "--porcelain"], "git could not list the worktrees")
            .ConfigureAwait(false);
        var mine = DirectoryOf(repository) + "/";
        var worktrees = ImmutableArray.CreateBuilder<GitWorktree>();
        foreach (var block in listed.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? path = null, head = null, branch = null;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    path = line["worktree ".Length..];
                else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                    head = line["HEAD ".Length..];
                else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                    branch = line["branch refs/heads/".Length..];
            }
            if (path is not null && path.StartsWith(mine, StringComparison.Ordinal) && !path[mine.Length..].Contains('/'))
                worktrees.Add(new GitWorktree(path[mine.Length..], path, branch, head ?? ""));
        }
        return [.. worktrees.OrderBy(worktree => worktree.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// A new worktree named <paramref name="name"/> on a new branch <c>corvid/&lt;name&gt;</c> from the <c>HEAD</c> of the
    /// workspace's working tree. The main checkout's branch, index and files are not touched. The worktree is made without
    /// its files first, so its configuration is read as git reads it while checking them out (its branch and its own git
    /// directory are what <c>includeIf "onbranch:"</c> and <c>"gitdir:"</c> match); a filter driver configured there refuses
    /// it before any file is written. Whatever fails once git has started is undone: the worktree, its record and the branch.
    /// </summary>
    /// <exception cref="WorktreeException">The name is not one, it or its branch exists, a filter driver is configured, or git failed.</exception>
    public async Task<GitWorktree> AddAsync(GitRepository repository, string name)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (CheckName(name) is { } invalid)
            throw new WorktreeException(invalid);
        var path = PathOf(repository, name);
        if (System.IO.Directory.Exists(path) || File.Exists(path))
            throw new WorktreeException($"{path} already exists.");
        var runner = Runner(repository.TopLevel);
        var branch = BranchPrefix + name;
        // Checked first, so undoing a failed add never deletes a branch Corvid did not make.
        if ((await RunRaw(runner, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], "git could not read the branches").ConfigureAwait(false))
            .ExitCode == 0)
            throw new WorktreeException($"The branch {branch} already exists; pick another name.");
        await RefuseFiltersAsync(runner, "create a worktree of it").ConfigureAwait(false);
        var head = (await Run(runner, ["rev-parse", "--verify", "HEAD^{commit}"], "git could not read the workspace's HEAD").ConfigureAwait(false)).Trim();
        System.IO.Directory.CreateDirectory(DirectoryOf(repository));
        try
        {
            await Run(runner, ["worktree", "add", "--no-checkout", "-b", branch, path, head], $"git could not create the worktree {name}")
                .ConfigureAwait(false);
            var inside = Runner(path);
            await RefuseFiltersAsync(inside, "create a worktree of it").ConfigureAwait(false);
            await Run(inside, ["reset", "--hard", "--quiet", head], $"git could not check the worktree {name} out").ConfigureAwait(false);
            return new GitWorktree(name, path, branch, head);
        }
        catch
        {
            await UndoAddAsync(repository, path, branch, head).ConfigureAwait(false);
            throw;
        }
    }

    // Best effort, each step on its own: the worktree (forced: nothing in it is anyone's work yet, and forced removal reads
    // no status, so no filter runs), its directory if git left it, git's record of it, and the branch, only while it still
    // points where it was made.
    private async Task UndoAddAsync(GitRepository repository, string path, string branch, string head)
    {
        var main = Runner(repository.MainCheckout);
        await TryRun(main, ["worktree", "remove", "--force", "--force", path]).ConfigureAwait(false);
        try
        {
            if (System.IO.Directory.Exists(path) && path.StartsWith(DirectoryOf(repository) + "/", StringComparison.Ordinal))
                System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the person; git's record of it stays until it is gone.
        }
        await TryRun(main, ["worktree", "prune"]).ConfigureAwait(false);
        await TryRun(main, ["update-ref", "-d", "refs/heads/" + branch, head]).ConfigureAwait(false);
    }

    private static async Task TryRun(GitRunner runner, IReadOnlyList<string> arguments)
    {
        try
        {
            await runner.RunRawAsync(arguments).ConfigureAwait(false);
        }
        catch (CheckpointException)
        {
            // Best effort.
        }
    }

    /// <summary>Removes one of Corvid's worktrees of the repository; its branch stays. git refuses one with changes or untracked files.</summary>
    /// <exception cref="WorktreeException">No such worktree of Corvid's, it has changes, a filter driver is configured, or git failed.</exception>
    public async Task<GitWorktree> RemoveAsync(GitRepository repository, string name)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (CheckName(name) is { } invalid)
            throw new WorktreeException(invalid);
        var worktree = (await ListAsync(repository).ConfigureAwait(false)).FirstOrDefault(item => item.Name == name)
            ?? throw new WorktreeException($"There is no worktree named {name} of {repository.MainCheckout} that Corvid made.");
        // git reads the worktree's status before removing it, which runs clean filters as the worktree's configuration names them.
        await RefuseFiltersAsync(Runner(repository.MainCheckout), "remove a worktree of it").ConfigureAwait(false);
        if (System.IO.Directory.Exists(worktree.Path))
            await RefuseFiltersAsync(Runner(worktree.Path), "remove the worktree").ConfigureAwait(false);
        await Run(Runner(repository.MainCheckout), ["worktree", "remove", worktree.Path], $"git did not remove the worktree {name}")
            .ConfigureAwait(false);
        return worktree;
    }

    /// <summary>
    /// Removes a worktree Corvid made for a while (a child's) when nothing was done in it: no changes, no untracked files and
    /// <c>HEAD</c> still at the commit it was made from (<see cref="GitWorktree.Head"/>); its branch goes too. Returns whether
    /// it was removed: one with work in it, or one git will not remove, is kept.
    /// </summary>
    public async Task<bool> RemoveIfUnusedAsync(GitRepository repository, GitWorktree worktree)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(worktree);
        try
        {
            if (!System.IO.Directory.Exists(worktree.Path) || worktree.Head.Length == 0)
                return false;
            var head = (await Run(Runner(worktree.Path), ["rev-parse", "--verify", "HEAD"], "git could not read the worktree's HEAD").ConfigureAwait(false)).Trim();
            if (head != worktree.Head)
                return false;
            // git's own removal deletes ignored files (build output, notes a child wrote under an ignored path): anything
            // status shows, ignored files included, is work, and the worktree is kept.
            var status = await Run(Runner(worktree.Path), ["status", "--porcelain", "--ignored", "--untracked-files=all"],
                "git could not read the worktree's status").ConfigureAwait(false);
            if (status.Trim().Length > 0)
                return false;
            await RemoveAsync(repository, worktree.Name).ConfigureAwait(false);
        }
        catch (WorktreeException)
        {
            return false;
        }
        if (worktree.Branch is { } branch)
            await TryRun(Runner(repository.MainCheckout), ["update-ref", "-d", "refs/heads/" + branch, worktree.Head]).ConfigureAwait(false);
        return true;
    }

    private const string FilterKeys = @"^filter\..*\.(smudge|clean|process)$";
    private const string IncludeKeys = @"^include(if\..*)?\.path$";
    private const int MaxIncludeDepth = 10;

    // A filter driver's smudge, clean or process command runs while git checks files out or reads a worktree's status,
    // outside any sandbox, and a tool in a workspace could have written it. Refused when one is configured as git run in that
    // directory reads its configuration (includes followed, their conditions evaluated there), and when any file the
    // configuration includes, under whatever condition, names one: a condition that holds elsewhere (another branch, another
    // git directory) cannot bring one in either.
    private async Task RefuseFiltersAsync(GitRunner runner, string action)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        var drivers = await RunRaw(runner, ["config", "--includes", "--get-regexp", FilterKeys], "git could not read the configuration").ConfigureAwait(false);
        if (drivers.ExitCode == 0)
            names.UnionWith(Keys(drivers.Text));
        var includes = await RunRaw(runner, ["config", "--no-includes", "--show-origin", "--get-regexp", IncludeKeys], "git could not read the configuration")
            .ConfigureAwait(false);
        if (includes.ExitCode == 0)
            await IncludedFiltersAsync(runner, includes.Text, names, new HashSet<string>(StringComparer.Ordinal), 0).ConfigureAwait(false);
        if (names.Count > 0)
            throw new WorktreeException($"The repository's configuration names filter commands ({string.Join(", ", names)}) that git would run " +
                $"outside the sandbox; Corvid will not {action}. Do it yourself, or remove the filter configuration first.");
    }

    // Every file an include.path or includeIf.*.path of the listing (git config --show-origin) names, read whatever its
    // condition: the filters it names, then the files it includes in turn.
    private async Task IncludedFiltersAsync(GitRunner runner, string listing, SortedSet<string> names, HashSet<string> seen, int depth)
    {
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0 || !line.StartsWith("file:", StringComparison.Ordinal))
                continue;
            var origin = line[5..tab];
            var space = line.IndexOf(' ', tab);
            if (space < 0)
                continue;
            var target = line[(space + 1)..].Trim();
            // git expands "~/" with HOME, which is /nonexistent for every git Corvid runs, so such a file is never read.
            if (target.Length == 0 || target.StartsWith('~') || target.StartsWith("%(", StringComparison.Ordinal))
                continue;
            var file = Path.IsPathFullyQualified(target) ? target
                : Path.GetFullPath(target, Path.GetDirectoryName(Path.GetFullPath(origin, runner.WorkingDirectory)) ?? "/");
            if (!seen.Add(file) || !File.Exists(file))
                continue;
            if (depth >= MaxIncludeDepth)
            {
                names.Add($"includes nested deeper than {MaxIncludeDepth} at {file}");
                continue;
            }
            var filters = await RunRaw(runner, ["config", "--file", file, "--no-includes", "--get-regexp", FilterKeys], "git could not read " + file)
                .ConfigureAwait(false);
            if (filters.ExitCode == 0)
                names.UnionWith(Keys(filters.Text));
            var nested = await RunRaw(runner, ["config", "--file", file, "--no-includes", "--show-origin", "--get-regexp", IncludeKeys],
                "git could not read " + file).ConfigureAwait(false);
            if (nested.ExitCode == 0)
                await IncludedFiltersAsync(runner, nested.Text, names, seen, depth + 1).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> Keys(string listing) =>
        listing.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', 2)[0]);

    private static async Task<GitResult> RunRaw(GitRunner runner, IReadOnlyList<string> arguments, string failure)
    {
        try
        {
            return await runner.RunRawAsync(arguments).ConfigureAwait(false);
        }
        catch (CheckpointException exception)
        {
            throw new WorktreeException($"{failure}: {exception.Message}", exception);
        }
    }

    private GitRunner Runner(string directory) =>
        _git is null
            ? throw new WorktreeException("git was not found, so Corvid cannot make worktrees (Corvid:Checkpoints:GitPath).")
            : GitRunner.ForDiscovery(_git, directory, null, _timeout);

    private static async Task<string> Run(GitRunner runner, IReadOnlyList<string> arguments, string failure)
    {
        try
        {
            var result = await runner.RunRawAsync(arguments).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new WorktreeException($"{failure}: {FirstLine(result.Error)}");
            return result.Text;
        }
        catch (CheckpointException exception)
        {
            throw new WorktreeException($"{failure}: {exception.Message}", exception);
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } line
            ? line.StartsWith("fatal: ", StringComparison.Ordinal) || line.StartsWith("error: ", StringComparison.Ordinal) ? line[7..] : line
            : "no reason given";
}
