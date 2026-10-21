using Corvid.Kernel.Checkpoints;
using Corvid.Memory;

namespace Corvid.Kernel.Sessions;

/// <summary>
/// Which project a workspace belongs to (C-025), for scoping lessons. The workspace's repository is found with the git CLI in
/// discovery mode (<see cref="GitRunner.ForDiscovery"/>: cleared environment, no system or global configuration, no hooks):
/// <c>rev-parse --git-common-dir</c>, so a linked worktree resolves to its main checkout, then <c>remote.origin.url</c> of
/// that repository. The id is the hash of the origin remote when there is one, else of the main checkout's path; without
/// git, outside a repository, or in a repository git refuses (dubious ownership), it is the hash of the workspace path.
/// </summary>
public static class ProjectResolver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <param name="git">
    /// The git executable: a path, or a name looked up on <c>PATH</c> (the checkpoints' setting). Null, or one that is not
    /// found, keys the workspace by its path.
    /// </param>
    /// <param name="home">
    /// The user's home directory. A workspace below it does not discover a repository at it or above it
    /// (<c>GIT_CEILING_DIRECTORIES</c>), so a dotfiles repository at <c>$HOME</c> does not capture every workspace that is
    /// not a repository. A workspace that is the home directory itself is keyed by its path.
    /// </param>
    public static async Task<ProjectIdentity> ResolveAsync(string workspace, string? git, string? home, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var byPath = new ProjectIdentity(ProjectIds.FromPath(root), ProjectSources.Path);
        git = git is null ? null : GitRunner.Resolve(git, Environment.GetEnvironmentVariable("PATH"));
        if (git is null || !Directory.Exists(root))
            return byPath;
        string? ceiling = null;
        if (home is { Length: > 0 } && Path.IsPathFullyQualified(home))
        {
            var homeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
            if (root == homeRoot)
                return byPath;
            if (root.StartsWith(homeRoot + "/", StringComparison.Ordinal))
                ceiling = homeRoot;
        }

        var runner = GitRunner.ForDiscovery(git, root, ceiling, timeout ?? DefaultTimeout);
        try
        {
            var common = await runner.RunRawAsync(["rev-parse", "--path-format=absolute", "--git-common-dir"]).ConfigureAwait(false);
            var commonDirectory = common.Text.Trim();
            if (common.ExitCode != 0 || commonDirectory.Length == 0 || !Path.IsPathFullyQualified(commonDirectory))
                return byPath;
            commonDirectory = Path.TrimEndingDirectorySeparator(commonDirectory);
            var remote = await runner.RunRawAsync(["--git-dir=" + commonDirectory, "config", "--get", "remote.origin.url"]).ConfigureAwait(false);
            var url = remote.Text.Trim();
            if (remote.ExitCode == 0 && url.Length > 0)
                return new ProjectIdentity(ProjectIds.FromRemote(url), ProjectSources.Remote);
            var checkout = Path.GetFileName(commonDirectory) == ".git" ? Path.GetDirectoryName(commonDirectory)! : commonDirectory;
            return new ProjectIdentity(ProjectIds.FromPath(checkout), ProjectSources.Checkout);
        }
        catch (CheckpointException)
        {
            // git could not start or did not finish: the path is still a project.
            return byPath;
        }
    }
}
