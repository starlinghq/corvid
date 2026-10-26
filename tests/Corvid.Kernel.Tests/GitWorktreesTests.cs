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

    // A filter whose commands leave a marker, in a file outside the repository that its configuration includes.
    private string EvilInclude(string marker)
    {
        var file = Path.Combine(_root, "evil-" + Guid.NewGuid().ToString("N")[..6] + ".inc");
        File.WriteAllText(file, $"[filter \"evil\"]\n\tsmudge = touch {marker}; cat\n\tclean = touch {marker}; cat\n");
        return file;
    }

    private static void CommitAttributes(string project)
    {
        File.WriteAllText(Path.Combine(project, ".gitattributes"), "* filter=evil\n");
        CheckpointFixture.Git(project, "add", ".gitattributes");
        CheckpointFixture.Git(project, "commit", "-q", "-m", "attributes");
    }

    // H1 (A6 review): conditions that hold only inside the new worktree (its branch corvid/<name>, its git directory under
    // .git/worktrees) once brought a filter in that the check in the main checkout never saw, and git ran it on checkout.
    [Theory]
    [InlineData("includeIf.onbranch:corvid/**.path")]
    [InlineData("includeIf.gitdir:**/worktrees/**.path")]
    [InlineData("includeIf.gitdir/i:**/WORKTREES/**.path")]
    [InlineData("includeIf.hasconfig:remote.*.url:https://example.com/**.path")]
    [InlineData("includeIf.onbranch:no-such-branch.path")]
    public async Task A_filter_any_include_brings_in_is_refused_and_nothing_is_left(string include)
    {
        var project = Repository();
        CommitAttributes(project);
        CheckpointFixture.Git(project, "remote", "add", "origin", "https://example.com/project.git");
        var marker = Path.Combine(_root, "filter-ran");
        CheckpointFixture.Git(project, "config", include, EvilInclude(marker));
        var worktrees = Worktrees();
        var repository = await worktrees.ResolveAsync(project);

        var refused = await Assert.ThrowsAsync<WorktreeException>(() => worktrees.AddAsync(repository, "filtered"));

        Assert.Contains("filter.evil.smudge", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.False(Directory.Exists(worktrees.PathOf(repository, "filtered")));
        Assert.Empty(await worktrees.ListAsync(repository));
        Assert.DoesNotContain("corvid/", CheckpointFixture.Git(project, "branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_is_refused_once_the_worktree_s_configuration_names_a_filter()
    {
        var project = Repository();
        CommitAttributes(project);
        var worktrees = Worktrees();
        var repository = await worktrees.ResolveAsync(project);
        var made = await worktrees.AddAsync(repository, "later");
        var marker = Path.Combine(_root, "filter-ran");
        // Written after the worktree was made, and active only there; its files' times changed, so git status rereads them.
        CheckpointFixture.Git(project, "config", "includeIf.onbranch:corvid/**.path", EvilInclude(marker));
        File.SetLastWriteTimeUtc(Path.Combine(made.Path, "README.md"), DateTime.UtcNow.AddMinutes(5));

        var refused = await Assert.ThrowsAsync<WorktreeException>(() => worktrees.RemoveAsync(repository, "later"));

        Assert.Contains("filter.evil.clean", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
        Assert.True(Directory.Exists(made.Path));
    }

    [Fact]
    public async Task A_worktree_nothing_was_done_in_goes_with_its_branch_and_one_with_work_stays()
    {
        var project = Repository();
        var worktrees = Worktrees();
        var repository = await worktrees.ResolveAsync(project);
        var unused = await worktrees.AddAsync(repository, "unused");
        var committed = await worktrees.AddAsync(repository, "committed");
        var dirty = await worktrees.AddAsync(repository, "dirty");
        var ignored = await worktrees.AddAsync(repository, "ignored");
        // Output under an ignored path (a build's out/): git's own removal would delete it, so it counts as work.
        File.AppendAllText(Path.Combine(project, ".git", "info", "exclude"), "out/\n");
        Directory.CreateDirectory(Path.Combine(ignored.Path, "out"));
        File.WriteAllText(Path.Combine(ignored.Path, "out", "result.txt"), "built\n");
        File.WriteAllText(Path.Combine(committed.Path, "new.txt"), "work\n");
        CheckpointFixture.Git(committed.Path, "add", ".");
        CheckpointFixture.Git(committed.Path, "commit", "-q", "-m", "work");
        File.WriteAllText(Path.Combine(dirty.Path, "README.md"), "changed\n");

        Assert.True(await worktrees.RemoveIfUnusedAsync(repository, unused));
        Assert.False(await worktrees.RemoveIfUnusedAsync(repository, committed));
        Assert.False(await worktrees.RemoveIfUnusedAsync(repository, dirty));
        Assert.False(await worktrees.RemoveIfUnusedAsync(repository, ignored));
        Assert.True(File.Exists(Path.Combine(ignored.Path, "out", "result.txt")));

        Assert.False(Directory.Exists(unused.Path));
        Assert.Equal(["committed", "dirty", "ignored"],
            (await worktrees.ListAsync(repository)).Select(worktree => worktree.Name).Order(StringComparer.Ordinal));
        var branches = CheckpointFixture.Git(project, "branch", "--list", "corvid/*");
        Assert.DoesNotContain("corvid/unused", branches, StringComparison.Ordinal);
        Assert.Contains("corvid/committed", branches, StringComparison.Ordinal);
    }

    // M3 (A6 review): an add that fails half way (here git's checkout outlasts the timeout) leaves no directory, record or branch.
    [Fact]
    public async Task An_add_that_times_out_leaves_nothing_behind()
    {
        var project = Repository();
        var slow = Path.Combine(_root, "slow-git");
        File.WriteAllText(slow, $"#!/bin/sh\nfor a in \"$@\"; do [ \"$a\" = reset ] && exec sleep 30; done\nexec {Git()} \"$@\"\n");
        File.SetUnixFileMode(slow, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var worktrees = new GitWorktrees(slow, Path.Combine(_root, "state", "corvid-worktrees"), TimeSpan.FromSeconds(2));
        var repository = await worktrees.ResolveAsync(project);

        var failed = await Assert.ThrowsAsync<WorktreeException>(() => worktrees.AddAsync(repository, "slow"));

        Assert.Contains("did not finish", failed.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(worktrees.PathOf(repository, "slow")));
        Assert.Empty(await worktrees.ListAsync(repository));
        Assert.DoesNotContain("corvid/slow", CheckpointFixture.Git(project, "branch", "--list"), StringComparison.Ordinal);
        Assert.DoesNotContain("slow", CheckpointFixture.Git(project, "worktree", "list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_branch_of_the_name_is_refused_and_kept()
    {
        var project = Repository();
        CheckpointFixture.Git(project, "branch", "corvid/taken");
        var worktrees = Worktrees();
        var repository = await worktrees.ResolveAsync(project);

        var refused = await Assert.ThrowsAsync<WorktreeException>(() => worktrees.AddAsync(repository, "taken"));

        Assert.Contains("already exists", refused.Message, StringComparison.Ordinal);
        Assert.Contains("corvid/taken", CheckpointFixture.Git(project, "branch", "--list"), StringComparison.Ordinal);
    }

    // M4 (A6 review): reached through a symbolic link, the directory is its real path, as git lists the worktrees.
    [Fact]
    public async Task A_worktrees_directory_behind_a_symbolic_link_lists_and_removes_its_worktrees()
    {
        var project = Repository();
        var real = Directory.CreateDirectory(Path.Combine(_root, "real-state")).FullName;
        var link = Path.Combine(_root, "linked-state");
        Directory.CreateSymbolicLink(link, real);
        var worktrees = new GitWorktrees(Git(), Path.Combine(link, "corvid-worktrees"));
        var repository = await worktrees.ResolveAsync(project);

        var made = await worktrees.AddAsync(repository, "linked");

        Assert.StartsWith(real, made.Path, StringComparison.Ordinal);
        Assert.Equal([made], await worktrees.ListAsync(repository));
        await worktrees.RemoveAsync(repository, "linked");
        Assert.False(Directory.Exists(made.Path));
    }

    [Fact]
    public void New_names_made_in_the_same_second_differ()
    {
        var now = DateTimeOffset.UtcNow;
        var names = Enumerable.Range(0, 20).Select(_ => GitWorktrees.NewName(now)).ToHashSet();
        Assert.True(names.Count > 1);
        Assert.All(names, name => Assert.Null(GitWorktrees.CheckName(name)));
    }

    [Fact]
    public async Task A_directory_outside_any_repository_is_refused()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        var error = await Assert.ThrowsAsync<WorktreeException>(() => Worktrees().ResolveAsync(plain));
        Assert.Contains("not in a git repository", error.Message, StringComparison.Ordinal);
    }
}
