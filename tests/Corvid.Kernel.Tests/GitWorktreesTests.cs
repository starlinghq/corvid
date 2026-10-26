using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Workspaces;

namespace Corvid.Kernel.Tests;

/// <summary>A6 (G-4): worktrees for sessions with the real git CLI in temporary directories; skipped where git is not installed.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class GitWorktreesTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "corvid-worktrees-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Git()
    {
        var git = GitRunner.Resolve("git", Environment.GetEnvironmentVariable("PATH"));
        if (git is null)
            Assert.Skip("git is not installed");
        return git;
    }

    // The working tree's files, without git's own (a worktree adds its record under .git/worktrees).
    private static List<KeyValuePair<string, string>> Files(SortedDictionary<string, string> fingerprint) =>
        [.. fingerprint.Where(pair => !pair.Key.StartsWith(".git/", StringComparison.Ordinal))];

    private GitWorktrees Worktrees() => new(Git(), Path.Combine(_root, "state", "corvid-worktrees"));

    private string Repository()
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        CheckpointFixture.Git(path, "init", "-q");
        File.WriteAllText(Path.Combine(path, "README.md"), "hello\n");
        Directory.CreateDirectory(Path.Combine(path, "src"));
        File.WriteAllText(Path.Combine(path, "src", "main.c"), "int main;\n");
        CheckpointFixture.Git(path, "add", ".");
        CheckpointFixture.Git(path, "commit", "-q", "-m", "first");
        return path;
    }

    [Fact]
    public async Task A_worktree_gets_its_own_branch_and_leaves_the_main_checkout_alone()
    {
        var project = Repository();
        // The person has their own work in progress on their branch.
        CheckpointFixture.Git(project, "switch", "-q", "-c", "feature");
        File.WriteAllText(Path.Combine(project, "README.md"), "edited by the person\n");
        File.WriteAllText(Path.Combine(project, "scratch.txt"), "untracked\n");
        var before = CheckpointFixture.Fingerprint(project);
        var status = CheckpointFixture.Git(project, "status", "--porcelain");
        var worktrees = Worktrees();

        var repository = await worktrees.ResolveAsync(project);
        var made = await worktrees.AddAsync(repository, "fix-1");

        Assert.Equal(new GitRepository(project, project, ""), repository);
        Assert.Equal(Path.Combine(worktrees.DirectoryOf(repository), "fix-1"), made.Path);
        Assert.StartsWith(Path.Combine(_root, "state", "corvid-worktrees", "project-"), made.Path, StringComparison.Ordinal);
        Assert.Equal("corvid/fix-1", made.Branch);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(made.Path, "README.md")));
        Assert.False(File.Exists(Path.Combine(made.Path, "scratch.txt")));
        // The main checkout: same branch, same files, same status.
        Assert.Equal("feature", CheckpointFixture.Git(project, "branch", "--show-current").Trim());
        Assert.Equal(Files(before), Files(CheckpointFixture.Fingerprint(project)));
        Assert.Equal(status, CheckpointFixture.Git(project, "status", "--porcelain"));
        Assert.Equal([made], await worktrees.ListAsync(repository));
        // The worktree resolves to the same repository, so its own worktrees sit beside it.
        Assert.Equal(project, (await worktrees.ResolveAsync(made.Path)).MainCheckout);
    }

    [Fact]
    public async Task A_workspace_in_a_subdirectory_is_kept_as_one_and_names_are_checked()
    {
        var project = Repository();
        var worktrees = Worktrees();

        var repository = await worktrees.ResolveAsync(Path.Combine(project, "src"));

        Assert.Equal("src", repository.Subdirectory);
        Assert.NotNull(GitWorktrees.CheckName("../escape"));
        Assert.NotNull(GitWorktrees.CheckName("Upper"));
        Assert.NotNull(GitWorktrees.CheckName("a.lock"));
        Assert.NotNull(GitWorktrees.CheckName(""));
        Assert.Null(GitWorktrees.CheckName("fix-1.2_b"));
        await Assert.ThrowsAsync<WorktreeException>(() => worktrees.AddAsync(repository, "../escape"));
        var made = await worktrees.AddAsync(repository, "one");
        Assert.Contains("already exists", (await Assert.ThrowsAsync<WorktreeException>(() => worktrees.AddAsync(repository, "one"))).Message,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(made.Path, "src", "main.c")));
    }

    [Fact]
    public async Task Removing_keeps_the_branch_and_refuses_a_worktree_with_changes()
    {
        var project = Repository();
        var worktrees = Worktrees();
        var repository = await worktrees.ResolveAsync(project);
        var kept = await worktrees.AddAsync(repository, "dirty");
        var clean = await worktrees.AddAsync(repository, "clean");
        File.WriteAllText(Path.Combine(kept.Path, "new.txt"), "work\n");

        var refused = await Assert.ThrowsAsync<WorktreeException>(() => worktrees.RemoveAsync(repository, "dirty"));
        await worktrees.RemoveAsync(repository, "clean");

        Assert.Contains("untracked", refused.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(kept.Path));
        Assert.False(Directory.Exists(clean.Path));
        Assert.Equal(["dirty"], (await worktrees.ListAsync(repository)).Select(worktree => worktree.Name));
        Assert.Contains("corvid/clean", CheckpointFixture.Git(project, "branch", "--list", "corvid/*"), StringComparison.Ordinal);
        // Only Corvid's own: the main checkout is not one.
        await Assert.ThrowsAsync<WorktreeException>(() => worktrees.RemoveAsync(repository, "project"));
    }

    [Fact]
    public async Task No_hook_runs_and_a_configured_filter_command_is_refused()
    {
        var project = Repository();
        var marker = Path.Combine(_root, "hook-ran");
        var hook = Path.Combine(project, ".git", "hooks", "post-checkout");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch {marker}\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var worktrees = Worktrees();
        var repository = await worktrees.ResolveAsync(project);

        await worktrees.AddAsync(repository, "hooked");
        Assert.False(File.Exists(marker));

        // A tool in the workspace could write the repository's configuration: a smudge filter would run on checkout.
        CheckpointFixture.Git(project, "config", "filter.evil.smudge", $"touch {marker}; cat");
        File.WriteAllText(Path.Combine(project, ".gitattributes"), "* filter=evil\n");
        var refused = await Assert.ThrowsAsync<WorktreeException>(() => worktrees.AddAsync(repository, "filtered"));
        Assert.Contains("filter.evil.smudge", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(worktrees.PathOf(repository, "filtered")));
    }

    [Fact]
    public async Task A_directory_outside_any_repository_is_refused()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        var error = await Assert.ThrowsAsync<WorktreeException>(() => Worktrees().ResolveAsync(plain));
        Assert.Contains("not in a git repository", error.Message, StringComparison.Ordinal);
    }
}
