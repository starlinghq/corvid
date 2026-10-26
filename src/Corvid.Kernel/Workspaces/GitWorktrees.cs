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
/// a cleared environment, no system or global configuration, hooks off. A repository whose own configuration names a
/// filter driver (which git would run while checking files out, unsandboxed) is refused, since a tool in a workspace can
/// write that configuration.
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
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
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

    /// <summary>A name for a new worktree from the time: <c>session-20261025-142233</c>.</summary>
    public static string NewName(DateTimeOffset now) => "session-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

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
    /// workspace's working tree. The main checkout's branch, index and files are not touched.
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
        await RefuseFiltersAsync(runner).ConfigureAwait(false);
        System.IO.Directory.CreateDirectory(DirectoryOf(repository));
        var branch = BranchPrefix + name;
        await Run(runner, ["worktree", "add", "-b", branch, path, "HEAD"], $"git could not create the worktree {name}").ConfigureAwait(false);
        var head = (await Run(Runner(path), ["rev-parse", "HEAD"], "git could not read the new worktree's HEAD").ConfigureAwait(false)).Trim();
        return new GitWorktree(name, path, branch, head);
    }

    /// <summary>Removes one of Corvid's worktrees of the repository; its branch stays. git refuses one with changes or untracked files.</summary>
    /// <exception cref="WorktreeException">No such worktree of Corvid's, it has changes, or git failed.</exception>
    public async Task<GitWorktree> RemoveAsync(GitRepository repository, string name)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (CheckName(name) is { } invalid)
            throw new WorktreeException(invalid);
        var worktree = (await ListAsync(repository).ConfigureAwait(false)).FirstOrDefault(item => item.Name == name)
            ?? throw new WorktreeException($"There is no worktree named {name} of {repository.MainCheckout} that Corvid made.");
        await Run(Runner(repository.MainCheckout), ["worktree", "remove", worktree.Path], $"git did not remove the worktree {name}")
            .ConfigureAwait(false);
        return worktree;
    }

    // A filter driver's smudge or process command runs on checkout, outside any sandbox; a tool could have written it.
    private async Task RefuseFiltersAsync(GitRunner runner)
    {
        var drivers = await runner.RunRawAsync(["config", "--includes", "--get-regexp", @"^filter\..*\.(smudge|process|clean)$"]).ConfigureAwait(false);
        if (drivers.ExitCode == 0 && drivers.Text.Trim() is { Length: > 0 } listed)
        {
            var names = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', 2)[0]).Distinct();
            throw new WorktreeException($"The repository's configuration names filter commands ({string.Join(", ", names)}) that git would run " +
                "while checking the worktree out, outside the sandbox; Corvid will not create a worktree of it. Create it yourself and open it as a workspace.");
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
