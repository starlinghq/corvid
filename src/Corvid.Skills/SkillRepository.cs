using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Corvid.Skills;

/// <summary>One commit in the skills repository.</summary>
public sealed record SkillCommit(string Revision, string Author, DateTimeOffset Date, string Subject);

/// <summary>
/// The skills directory as a git repository (plan §11): every change is a commit whose author is <c>user</c>,
/// <c>agent</c> or <c>hub:&lt;source&gt;</c>. A file's author is the author of the last commit that touched it, so a user
/// edit makes a file the user's, and the curator and learning loop (which may change only agent-authored files) leave it
/// alone. Edits made on disk outside Corvid are committed as the user before any other change.
/// </summary>
/// <remarks>Calls are serialized: git takes its own index lock, and two Corvid writers would race for it.</remarks>
public sealed partial class SkillRepository
{
    // Keys `git init` writes and nothing else. Any other key in the repository's own configuration (a hooks path, a filter
    // or diff driver, an fsmonitor) could run code from git, so the repository is refused until it is removed.
    private static readonly ImmutableHashSet<string> AllowedConfig = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "core.repositoryformatversion", "core.filemode", "core.bare", "core.logallrefupdates", "core.ignorecase",
        "core.precomposeunicode", "core.symlinks", "core.autocrlf");

    private readonly GitCli _git;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SkillRepository(GitCli git) => _git = git;

    public string Root => _git.WorkTree;

    /// <summary>Whether <paramref name="executable"/> runs as git (<c>corvid doctor</c>).</summary>
    public static bool IsGitAvailable(string executable = "git") => GitCli.IsAvailable(executable);

    /// <summary>Opens the repository at <paramref name="root"/>, creating the directory and the repository if needed.</summary>
    /// <exception cref="GitException">git is missing, or the repository is not one Corvid can use safely.</exception>
    public static async Task<SkillRepository> OpenAsync(string root, string gitExecutable = "git", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        Directory.CreateDirectory(full);
        var git = new GitCli(full, gitExecutable);
        var dotGit = Path.Combine(full, ".git");
        var info = new FileInfo(dotGit);
        if (info.Exists || info.LinkTarget is not null || new DirectoryInfo(dotGit).LinkTarget is not null)
            throw new GitException($"{dotGit} is a file or a link, not a repository directory; Corvid does not follow it.");
        if (!Directory.Exists(dotGit))
        {
            var init = await git.RunAsync(["init", "--quiet", full], null, gitDir: false, cancellationToken).ConfigureAwait(false);
            if (!init.Succeeded)
                throw new GitException("git init failed: " + init.Error.Trim());
        }
        var repository = new SkillRepository(git);
        await repository.VerifyConfigAsync(cancellationToken).ConfigureAwait(false);
        return repository;
    }

    private async Task VerifyConfigAsync(CancellationToken cancellationToken)
    {
        var result = await _git.RunAsync(["config", "--local", "--list", "--name-only"], null, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new GitException("cannot read the skills repository's configuration: " + result.Error.Trim());
        var unexpected = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(key => !AllowedConfig.Contains(key)).ToArray();
        if (unexpected.Length > 0)
            throw new GitException($"the skills repository's .git/config sets {string.Join(", ", unexpected)}, which could run code; remove them to use it.");
        var hooks = Path.Combine(Root, ".git", "hooks");
        // Hooks are never run (core.hooksPath points at nothing); the samples git init writes are removed so nobody mistakes them.
        if (Directory.Exists(hooks))
            foreach (var sample in Directory.EnumerateFiles(hooks, "*.sample"))
                File.Delete(sample);
    }

    /// <summary>Commits everything changed on disk since the last commit as the user's, and returns the new revision if any.</summary>
    public async Task<string?> RecordOutsideChangesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CommitLockedAsync(null, SkillAuthor.User, "Record changes made outside Corvid", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the working tree and commits what it changed under <paramref name="path"/> as
    /// <paramref name="author"/>. Outside edits are first committed as the user's, so they are never attributed to another
    /// author. Returns the new revision, or null when nothing changed.
    /// </summary>
    /// <param name="check">Runs after outside edits are recorded and before the change, under the same lock (an authorship check).</param>
    public async Task<string?> CommitAsync(string path, SkillAuthor author, string message, Func<CancellationToken, Task> change,
        Func<CancellationToken, Task>? check = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(change);
        var relative = Relative(path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CommitLockedAsync(null, SkillAuthor.User, "Record changes made outside Corvid", cancellationToken).ConfigureAwait(false);
            if (check is not null)
                await check(cancellationToken).ConfigureAwait(false);
            await change(cancellationToken).ConfigureAwait(false);
            return await CommitLockedAsync(relative, author, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> CommitLockedAsync(string? relative, SkillAuthor author, string message, CancellationToken cancellationToken)
    {
        string[] scope = relative is null ? [] : ["--", relative.Length == 0 ? "." : relative];
        await _git.CheckedAsync(["add", "--all", .. scope], cancellationToken: cancellationToken).ConfigureAwait(false);
        var staged = await _git.RunAsync(["diff", "--cached", "--quiet", "--no-ext-diff"], null, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (staged.ExitCode == 0)
            return null;
        await _git.CheckedAsync(["commit", "--quiet", "--no-verify", "--allow-empty-message", "-m", message], author, cancellationToken)
            .ConfigureAwait(false);
        return (await _git.CheckedAsync(["rev-parse", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
    }

    /// <summary>The author of the last commit that touched each tracked file under <paramref name="path"/>.</summary>
    public async Task<ImmutableDictionary<string, string>> AuthorsAsync(string path, CancellationToken cancellationToken = default)
    {
        var relative = Relative(path);
        var files = (await _git.CheckedAsync(["ls-files", "-z", "--", relative.Length == 0 ? "." : relative], cancellationToken: cancellationToken)
            .ConfigureAwait(false)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var authors = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (files.Length == 0)
            return authors.ToImmutable();
        // One walk of the history, newest first: the first commit that names a file is the last to touch it.
        var tracked = files.ToHashSet(StringComparer.Ordinal);
        var walk = await _git.CheckedAsync(["-c", "core.quotePath=false", "log", "--no-renames", "--format=%x1e%an", "--name-only", "--",
            relative.Length == 0 ? "." : relative], cancellationToken: cancellationToken).ConfigureAwait(false);
        string? author = null;
        foreach (var line in walk.Split('\n'))
        {
            if (line.StartsWith('\x1e'))
                author = line[1..];
            else if (author is not null && line.Length > 0 && tracked.Contains(line))
                authors.TryAdd(line, author);
        }
        // A name the walk could not show plainly (a newline in it, say) is asked about on its own.
        foreach (var file in files.Where(file => !authors.ContainsKey(file)))
            authors[file] = (await _git.CheckedAsync(["log", "-1", "--format=%an", "--", file], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
        return authors.ToImmutable();
    }

    /// <summary>Whether anything under <paramref name="path"/> differs from the last commit (an edit on disk not yet recorded).</summary>
    public async Task<bool> IsDirtyAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        string[] scope = path is null ? [] : ["--", Relative(path) is { Length: > 0 } relative ? relative : "."];
        var status = await _git.CheckedAsync(["status", "--porcelain", "--untracked-files=all", .. scope], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return status.Trim().Length > 0;
    }

    public async Task<ImmutableArray<SkillCommit>> LogAsync(string? path = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        string[] scope = path is null ? [] : ["--", Relative(path) is { Length: > 0 } relative ? relative : "."];
        var result = await _git.RunAsync(["log", $"-n{Math.Clamp(limit, 1, 500)}", "--format=%H%x1f%an%x1f%aI%x1f%s", .. scope], null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // A repository without commits has no log yet.
        if (!result.Succeeded)
            return [];
        return [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\x1f'))
            .Where(parts => parts.Length == 4)
            .Select(parts => new SkillCommit(parts[0], parts[1], DateTimeOffset.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), parts[3]))];
    }

    /// <summary>The patch a commit made (<c>/skills diff</c>), at most <paramref name="maxChars"/> characters.</summary>
    public async Task<string> ShowAsync(string revision, int maxChars = 16_000, CancellationToken cancellationToken = default)
    {
        var checkedRevision = CheckRevision(revision);
        var output = await _git.CheckedAsync(["show", "--no-ext-diff", "--no-textconv", "--no-color", "--stat", "--patch",
            "--format=commit %H%nauthor %an%ndate %aI%n%n    %s%n", checkedRevision, "--"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return output.Length > maxChars ? output[..maxChars] + "\n… [diff cut]" : output;
    }

    /// <summary>Reverts a commit as <paramref name="author"/> (<c>/skills revert</c>), returning the new revision.</summary>
    public async Task<string> RevertAsync(string revision, SkillAuthor author, CancellationToken cancellationToken = default)
    {
        var checkedRevision = CheckRevision(revision);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CommitLockedAsync(null, SkillAuthor.User, "Record changes made outside Corvid", cancellationToken).ConfigureAwait(false);
            var result = await _git.RunAsync(["revert", "--no-edit", "--no-gpg-sign", checkedRevision], author, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                await _git.RunAsync(["revert", "--abort"], null, cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new GitException($"cannot revert {revision}: {(result.Error.Length > 0 ? result.Error : result.Output).Trim()}");
            }
            return (await _git.CheckedAsync(["rev-parse", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false)).Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A revision as typed: a hash prefix or <c>HEAD</c>, <c>HEAD~n</c>. Anything else could be read as an option.</summary>
    internal static string CheckRevision(string revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        return RevisionPattern().IsMatch(revision) ? revision : throw new ArgumentException($"'{revision}' is not a commit: give a hash, HEAD or HEAD~n.", nameof(revision));
    }

    private string Relative(string path)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
        var relative = Path.GetRelativePath(Root, full);
        if (relative == ".")
            return "";
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"{path} is outside the skills repository.", nameof(path));
        return relative.Replace('\\', '/');
    }

    [GeneratedRegex("^(HEAD(~[0-9]{1,4})?|[0-9a-f]{4,40})$")]
    private static partial Regex RevisionPattern();
}
