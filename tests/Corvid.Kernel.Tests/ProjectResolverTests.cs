using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Sessions;
using Corvid.Memory;

namespace Corvid.Kernel.Tests;

/// <summary>Project identity (C-025) with the real git CLI; skipped where git is not installed.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ProjectResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "corvid-project-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Git()
    {
        var git = GitRunner.Resolve("git", Environment.GetEnvironmentVariable("PATH"));
        if (git is null)
            Assert.Skip("git is not installed");
        return git;
    }

    private string Repository(string name, string? origin = null)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        CheckpointFixture.Git(path, "init", "-q");
        if (origin is not null)
            CheckpointFixture.Git(path, "remote", "add", "origin", origin);
        File.WriteAllText(Path.Combine(path, "README.md"), "hello\n");
        CheckpointFixture.Git(path, "add", "README.md");
        CheckpointFixture.Git(path, "commit", "-q", "-m", "first");
        return path;
    }

    [Fact]
    public async Task A_repository_with_an_origin_is_keyed_by_its_remote()
    {
        var repository = Repository("widgets", "git@github.com:example/widgets.git");
        var identity = await ProjectResolver.ResolveAsync(repository, Git(), null);
        Assert.Equal(new ProjectIdentity(ProjectIds.FromRemote("https://github.com/example/widgets"), ProjectSources.Remote), identity);

        // A subdirectory belongs to the same project.
        var sub = Directory.CreateDirectory(Path.Combine(repository, "src", "deep")).FullName;
        Assert.Equal(identity, await ProjectResolver.ResolveAsync(sub, Git(), null));
    }

    [Fact]
    public async Task A_linked_worktree_resolves_to_its_main_checkout()
    {
        var main = Repository("main");
        var linked = Path.Combine(_root, "linked");
        CheckpointFixture.Git(main, "worktree", "add", "-q", "-b", "feature", linked);

        var identity = await ProjectResolver.ResolveAsync(linked, Git(), null);
        Assert.Equal(new ProjectIdentity(ProjectIds.FromPath(main), ProjectSources.Checkout), identity);
        Assert.Equal(identity, await ProjectResolver.ResolveAsync(main, Git(), null));

        // With an origin, the worktree has the remote's id too.
        CheckpointFixture.Git(main, "remote", "add", "origin", "https://example.org/team/main.git");
        Assert.Equal(ProjectIds.FromRemote("ssh://git@example.org/team/main"), (await ProjectResolver.ResolveAsync(linked, Git(), null)).Id);
    }

    [Fact]
    public async Task Without_git_or_a_repository_the_path_is_the_key()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        var byPath = new ProjectIdentity(ProjectIds.FromPath(plain), ProjectSources.Path);
        Assert.Equal(byPath, await ProjectResolver.ResolveAsync(plain, null, null));
        Assert.Equal(byPath, await ProjectResolver.ResolveAsync(plain, "/nonexistent/bin/git", null));
        Assert.Equal(byPath, await ProjectResolver.ResolveAsync(plain, Git(), null));
    }

    [Fact]
    public async Task A_repository_at_home_does_not_capture_workspaces_below_it()
    {
        // A dotfiles repository at $HOME, and a plain directory inside it.
        var home = Repository("home", "https://example.org/me/dotfiles.git");
        var plain = Directory.CreateDirectory(Path.Combine(home, "notes")).FullName;

        Assert.Equal(ProjectSources.Remote, (await ProjectResolver.ResolveAsync(plain, Git(), null)).Source);
        Assert.Equal(new ProjectIdentity(ProjectIds.FromPath(plain), ProjectSources.Path), await ProjectResolver.ResolveAsync(plain, Git(), home));
        // The home directory itself is keyed by its path, not by the dotfiles repository.
        Assert.Equal(new ProjectIdentity(ProjectIds.FromPath(home), ProjectSources.Path), await ProjectResolver.ResolveAsync(home, Git(), home));
        // A real repository below home is still found.
        var project = Path.Combine(home, "code");
        Directory.CreateDirectory(project);
        CheckpointFixture.Git(project, "init", "-q");
        Assert.Equal(new ProjectIdentity(ProjectIds.FromPath(project), ProjectSources.Checkout), await ProjectResolver.ResolveAsync(project, Git(), home));
    }

    [Fact]
    public async Task Repository_hooks_and_the_users_configuration_never_run()
    {
        var repository = Repository("hooked", "https://example.org/team/hooked.git");
        var marker = Path.Combine(_root, "fsmonitor-ran");
        // An fsmonitor command in the repository's own configuration: git would run it on status, never on rev-parse or
        // config, and SafeConfig turns it off for every invocation anyway.
        CheckpointFixture.Git(repository, "config", "core.fsmonitor", $"touch {marker}");
        Assert.Equal(ProjectSources.Remote, (await ProjectResolver.ResolveAsync(repository, Git(), null)).Source);
        Assert.False(File.Exists(marker));
    }
}
