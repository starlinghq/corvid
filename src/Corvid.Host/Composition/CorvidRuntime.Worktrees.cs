using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Commands;
using Corvid.Host.Configuration;
using Corvid.Kernel.Workspaces;

namespace Corvid.Host.Composition;

/// <summary>The person did not approve a worktree's creation or removal, or nobody could be asked.</summary>
public sealed class WorktreeRefusedException(string message) : InvalidOperationException(message);

/// <summary>A worktree Corvid made, as surfaces list it: where it is, its branch, and the sessions of this log that work in it.</summary>
public sealed record WorktreeSummary(GitWorktree Worktree, ImmutableArray<string> Sessions);

/// <summary>
/// A6 (G-4): git worktrees per session. <see cref="CreateWorktreeAsync"/> makes one of a session's repository (a new branch
/// <c>corvid/&lt;name&gt;</c> from the workspace's <c>HEAD</c>, under <see cref="CorvidPaths.Worktrees"/>), and
/// <see cref="OpenWorktreeSessionAsync"/> starts a session in it; <c>delegate_task</c>'s <c>isolation: worktree</c> gives each such
/// child its own. Creating and removing one writes outside the workspace, so each asks the person through the asking
/// session's approvals whatever its mode or remembered rules say, blocks until answered, and is refused with nobody to ask.
/// The main checkout's branch, index and files are never touched; a removed worktree's branch stays.
/// </summary>
public sealed partial class CorvidRuntime
{
    // The approval context's state for a question that is never put to the decider (manual mode asks the person).
    private static readonly TurnState NoState = new(JsonDocument.Parse("{}").RootElement.Clone(), 0, false, []);

    /// <summary>The worktrees Corvid makes, with the checkpoints' git and <see cref="CorvidPaths.Worktrees"/>.</summary>
    public GitWorktrees Worktrees => LazyInitializer.EnsureInitialized(ref _worktrees,
        () => new GitWorktrees(Options.Checkpoints.GitPath, CorvidPaths.Worktrees(Options)));

    private GitWorktrees? _worktrees;

    /// <summary>The repository a session's workspace is in.</summary>
    /// <exception cref="WorktreeException">Not a repository Corvid can make worktrees of, or no git.</exception>
    public Task<GitRepository> RepositoryOfAsync(string sessionId) => Worktrees.ResolveAsync(WorkspaceServicesOf(sessionId).Root);

    /// <summary>Corvid's worktrees of a session's repository, each with the sessions of this log that work in it.</summary>
    public async Task<ImmutableArray<WorktreeSummary>> ListWorktreesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var repository = await RepositoryOfAsync(sessionId).ConfigureAwait(false);
        var worktrees = await Worktrees.ListAsync(repository).ConfigureAwait(false);
        var sessions = Log.ListSessions(cancellationToken).Select(id => (Id: id, Workspace: WorkspaceOf(id))).Where(item => !item.Workspace.IsDefault).ToList();
        return [.. worktrees.Select(worktree => new WorktreeSummary(worktree, [.. sessions
            .Where(item => item.Workspace.Path == worktree.Path || item.Workspace.Path.StartsWith(worktree.Path + "/", StringComparison.Ordinal))
            .Select(item => item.Id)]))];
    }

    /// <summary>
    /// Makes a worktree of <paramref name="sessionId"/>'s repository, after asking the person in that session (it must be
    /// open here with a surface attached). Returns where a session in it works: the worktree, or the same subdirectory of it
    /// the asking session works in.
    /// </summary>
    /// <param name="name">The worktree's name (its branch is <c>corvid/&lt;name&gt;</c>); one from the time when null.</param>
    /// <param name="callId">The tool call that asked for it (a <c>delegate_task</c>), shown with the approval.</param>
    /// <exception cref="WorktreeRefusedException">The person said no, or nobody could be asked.</exception>
    /// <exception cref="WorktreeException">Not a repository, a bad or taken name, a filter driver configured, or git failed.</exception>
    public async Task<SessionWorkspace> CreateWorktreeAsync(string sessionId, string? name = null, string? callId = null,
        CancellationToken cancellationToken = default)
    {
        name ??= GitWorktrees.NewName(DateTimeOffset.UtcNow);
        if (GitWorktrees.CheckName(name) is { } invalid)
            throw new WorktreeException(invalid);
        var repository = await RepositoryOfAsync(sessionId).ConfigureAwait(false);
        var path = Worktrees.PathOf(repository, name);
        var branch = GitWorktrees.BranchPrefix + name;
        await AskAsync(sessionId, $"git -C {Quote(repository.TopLevel)} worktree add -b {branch} {Quote(path)} HEAD",
            $"creates a git worktree outside the workspace, at {path}, on a new branch {branch}", callId, cancellationToken).ConfigureAwait(false);
        var made = await Worktrees.AddAsync(repository, name).ConfigureAwait(false);
        var workspace = repository.Subdirectory.Length == 0 ? made.Path : Path.Combine(made.Path, repository.Subdirectory);
        return new SessionWorkspace(workspace, repository.MainCheckout, made.Branch);
    }

    /// <summary>
    /// <see cref="CreateWorktreeAsync"/>, then a new session in the worktree (driven here until the surface lets it go).
    /// </summary>
    public async Task<CorvidSession> OpenWorktreeSessionAsync(string askingSessionId, string? name = null, string surface = "cli",
        CancellationToken cancellationToken = default)
    {
        var workspace = await CreateWorktreeAsync(askingSessionId, name, cancellationToken: cancellationToken).ConfigureAwait(false);
        return OpenSession(surface: surface, settings: new SessionSettings { Workspace = workspace.Path, Worktree = workspace });
    }

    /// <summary>
    /// Removes one of Corvid's worktrees of <paramref name="sessionId"/>'s repository after asking the person there. Its branch
    /// stays; git refuses one with changes or untracked files. Refused while a session that works in it is open here, or is
    /// the one asking.
    /// </summary>
    /// <exception cref="WorktreeRefusedException">The person said no, nobody could be asked, or a session here works in it.</exception>
    /// <exception cref="WorktreeException">No such worktree of Corvid's, it has changes, or git failed.</exception>
    public async Task<GitWorktree> RemoveWorktreeAsync(string sessionId, string name, CancellationToken cancellationToken = default)
    {
        if (GitWorktrees.CheckName(name) is { } invalid)
            throw new WorktreeException(invalid);
        var repository = await RepositoryOfAsync(sessionId).ConfigureAwait(false);
        var worktree = (await Worktrees.ListAsync(repository).ConfigureAwait(false)).FirstOrDefault(item => item.Name == name)
            ?? throw new WorktreeException($"There is no worktree named {name} of {repository.MainCheckout} that Corvid made.");
        bool Inside(string path) => path == worktree.Path || path.StartsWith(worktree.Path + "/", StringComparison.Ordinal);
        if (_sessions.Keys.FirstOrDefault(id => WorkspaceOf(id) is { IsDefault: false } open && Inside(open.Path)) is { } busy)
            throw new WorktreeRefusedException($"Session {TerminalText.Visible(busy)} works in {worktree.Path} and is open here; close it first.");
        await AskAsync(sessionId, $"git -C {Quote(repository.MainCheckout)} worktree remove {Quote(worktree.Path)}",
            $"removes the git worktree at {worktree.Path}, outside the workspace (its branch {worktree.Branch ?? "(detached)"} stays)", null,
            cancellationToken).ConfigureAwait(false);
        var removed = await Worktrees.RemoveAsync(repository, name).ConfigureAwait(false);
        // Its services go; a session recorded there is refused from now on, never moved.
        foreach (var key in _workspaces.Keys.Where(Inside))
            _workspaces.TryRemove(key, out _);
        return removed;
    }

    private async Task AskAsync(string sessionId, string command, string why, string? callId, CancellationToken cancellationToken)
    {
        if (!TryGetSession(sessionId, out var session))
            throw new WorktreeRefusedException($"Session {TerminalText.Visible(sessionId)} is not open here to ask the person in.");
        var context = new ApprovalContext(sessionId, ApprovalMode.Manual, NoState)
        {
            Interactive = session.Interactive,
            Tainted = session.Tainted,
            CallId = callId,
            AlwaysAsk = why,
        };
        var result = await session.Workspace.Approvals.AuthorizeAsync(context, new ShellAction(command), cancellationToken).ConfigureAwait(false);
        if (!result.Allowed)
            throw new WorktreeRefusedException(result.Resolution switch
            {
                ApprovalResolution.NoApprover => "Nobody could be asked to approve the worktree, so it was not made.",
                ApprovalResolution.TimedOut => "The worktree was not approved in time.",
                ApprovalResolution.Hardline => "The worktree's path is one Corvid never writes: " + result.Assessment.Summary,
                _ => "The worktree was not approved.",
            });
    }

    private static string Quote(string path) =>
        path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-') ? path : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
