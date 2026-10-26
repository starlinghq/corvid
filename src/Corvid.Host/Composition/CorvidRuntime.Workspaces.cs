using System.Collections.Concurrent;
using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Composition;

/// <summary>A session's workspace is not usable: the directory is gone, or Corvid will not work there.</summary>
public sealed class WorkspaceUnavailableException(string message) : InvalidOperationException(message);

/// <summary>Where a session works, as its log records it (<see cref="WorkspaceBound"/>), without building anything.</summary>
/// <param name="Path">The workspace's absolute path.</param>
/// <param name="Repository">The main checkout, when the workspace is a worktree Corvid created; else null.</param>
/// <param name="Branch">The worktree's branch, when it is one; else null.</param>
/// <param name="IsDefault">
/// The session recorded no workspace (a log written before A6; every session since records one), so it works in the
/// runtime's configured one. A session that recorded the configured workspace is not this.
/// </param>
public sealed record SessionWorkspace(string Path, string? Repository = null, string? Branch = null, bool IsDefault = false);

/// <summary>
/// A6 (G-4): a workspace per session. A session records its workspace when it is created (<see cref="WorkspaceBound"/>,
/// <see cref="SessionSettings.Workspace"/>), and its approvals, sandboxed shell, file tools, checkpoints, Review and Rewind and
/// project follow it (<see cref="WorkspaceServices"/>, built once per directory). Every new session records one, the
/// configured workspace when none was asked for (A6 review), so a process started elsewhere resumes it where it began; only
/// a log written before A6 recorded none and works in the configured workspace, as every session did before.
/// </summary>
public sealed partial class CorvidRuntime
{
    // How far into a log the binding is looked for: it is the first event, or the second after a child's ChildOf.
    private const int BindingWindow = 4;

    private readonly ConcurrentDictionary<string, Lazy<WorkspaceServices>> _workspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SessionWorkspace> _sessionWorkspaces = new(StringComparer.Ordinal);
    private WorkspaceServices? _defaultWorkspace;

    /// <summary>Builds the workspaces other than the configured one; null for a runtime that cannot (then only the default works).</summary>
    internal WorkspaceComposer? Workspaces { get; init; }

    /// <summary>The configured workspace (<c>Corvid:Workspace</c>, else the directory the process started in).</summary>
    public WorkspaceServices DefaultWorkspace => LazyInitializer.EnsureInitialized(ref _defaultWorkspace, () => new WorkspaceServices
    {
        Root = Canonical(Builtin.Environment.Workspace),
        Environment = Builtin.Environment,
        Approvals = Approvals,
        Shell = Builtin.Shell,
        Tools = Tools,
        Checkpoints = Checkpoints,
        Project = Project,
        SkillsRefusal = Skills.Store is null && Skills.State.StartsWith("off: the workspace", StringComparison.Ordinal) ? Skills.State : null,
        IsDefault = true,
    });

    /// <summary>The workspaces sessions of this process work in so far, the configured one first.</summary>
    public IReadOnlyList<WorkspaceServices> OpenWorkspaces =>
        [DefaultWorkspace, .. _workspaces.Values.Where(lazy => lazy.IsValueCreated).Select(lazy => lazy.Value)
            .Where(scope => !scope.IsDefault).OrderBy(scope => scope.Root, StringComparer.Ordinal)];

    /// <summary>
    /// The services of the workspace at <paramref name="path"/>, built the first time and shared after. The path must be an
    /// existing directory, given absolute; Corvid refuses a workspace inside its own state, Vault's or a credential store,
    /// where the tools would reach what the approvals protect.
    /// </summary>
    /// <exception cref="WorkspaceUnavailableException">Why not, in a sentence (the path shown with hidden characters escaped).</exception>
    public WorkspaceServices WorkspaceAt(string path)
    {
        var root = CheckWorkspace(path);
        RefuseRemoving(root);
        if (root == DefaultWorkspace.Root)
            return DefaultWorkspace;
        if (Workspaces is not { } composer)
            throw new WorkspaceUnavailableException($"This runtime works only in {TerminalText.Visible(DefaultWorkspace.Root)}.");
        var lazy = _workspaces.GetOrAdd(root, key => new Lazy<WorkspaceServices>(() =>
            composer.Build(key, Approvals, Tools, lessons: Memory?.Lessons is not null), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _workspaces.TryRemove(KeyValuePair.Create(root, lazy));
            throw;
        }
    }

    /// <summary>
    /// The canonical path of a directory a session may work in, or why not. Symbolic links are resolved, so two spellings of
    /// one directory are one workspace.
    /// </summary>
    /// <exception cref="WorkspaceUnavailableException">Not absolute, not a directory, or inside what Corvid protects.</exception>
    public string CheckWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new WorkspaceUnavailableException("A workspace is a directory; none was named.");
        var shown = TerminalText.Visible(path);
        if (!Path.IsPathFullyQualified(path))
            throw new WorkspaceUnavailableException($"A workspace is named by its absolute path, not '{shown}'.");
        if (!Directory.Exists(path))
            throw new WorkspaceUnavailableException($"The workspace {shown} is not a directory that exists.");
        var root = Canonical(path);
        var guarded = Builtin.Environment.Protected;
        foreach (var protectedPath in guarded.Vault.Concat(guarded.CredentialStores).Concat(guarded.SafetyConfiguration))
        {
            if (!Path.IsPathFullyQualified(protectedPath))
                continue;
            foreach (var candidate in new[] { Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath)), Canonical(protectedPath) })
                if (root == candidate || root.StartsWith(candidate + "/", StringComparison.Ordinal))
                    throw new WorkspaceUnavailableException(
                        $"Corvid will not work in {TerminalText.Visible(root)}: it is inside {TerminalText.Visible(candidate)}, which the approvals protect.");
        }
        return root;
    }

    /// <summary>Where a session works, from its log; the configured workspace when it recorded none (or has no events yet).</summary>
    public SessionWorkspace WorkspaceOf(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessionWorkspaces.TryGetValue(sessionId, out var known))
            return known;
        var events = Log.Read(sessionId, 0, long.MaxValue, BindingWindow);
        var bound = events.Select(stored => stored.Payload).OfType<WorkspaceBound>().FirstOrDefault();
        var workspace = bound is not null
            ? new SessionWorkspace(bound.Path, bound.Repository, bound.Branch)
            : new SessionWorkspace(DefaultWorkspace.Root, IsDefault: true);
        // A session with no events yet may still be created in another workspace, so only a written log is remembered.
        if (events.Count > 0)
            _sessionWorkspaces[sessionId] = workspace;
        return workspace;
    }

    /// <summary>The services a session works with: its recorded workspace's, built when first needed.</summary>
    /// <exception cref="WorkspaceUnavailableException">The session's workspace is gone (a removed worktree, say).</exception>
    public WorkspaceServices WorkspaceServicesOf(string sessionId)
    {
        var workspace = WorkspaceOf(sessionId);
        if (workspace.IsDefault)
            return DefaultWorkspace;
        try
        {
            return WorkspaceAt(workspace.Path);
        }
        catch (WorkspaceUnavailableException exception)
        {
            // Never another directory instead: the session's calls, checkpoints and rules were made for this one.
            throw new WorkspaceUnavailableException($"Session {TerminalText.Visible(sessionId)} works in {TerminalText.Visible(workspace.Path)}, " +
                $"and it cannot be used now: {exception.Message}");
        }
    }

    /// <summary>
    /// Records where a new session works (its first event, or second after <paramref name="first"/>), or checks that an
    /// existing one already works there. Returns the workspace's services.
    /// </summary>
    private WorkspaceServices BindWorkspace(string sessionId, string path, SessionWorkspace? worktree = null)
    {
        var scope = WorkspaceAt(path);
        if (Log.GetHead(sessionId) == 0)
        {
            try
            {
                Log.Append(sessionId, 0, [EventData.Create(new WorkspaceBound(scope.Root, worktree?.Repository, worktree?.Branch))]);
                _sessionWorkspaces[sessionId] = new SessionWorkspace(scope.Root, worktree?.Repository, worktree?.Branch);
                return scope;
            }
            catch (EventLogConflictException)
            {
                // Another surface wrote first; it is checked below like any existing session.
            }
        }
        var recorded = WorkspaceOf(sessionId);
        if (Canonical(recorded.Path) != scope.Root)
            throw new WorkspaceUnavailableException($"Session {TerminalText.Visible(sessionId)} works in {TerminalText.Visible(recorded.Path)}, " +
                $"not {TerminalText.Visible(scope.Root)}; a session keeps its workspace. Start a new session there instead.");
        return scope;
    }

    /// <summary>
    /// A new session with no workspace asked for records the configured one; an existing one is checked usable (a session
    /// whose workspace is gone fails here). Another surface that recorded one first is taken at its word.
    /// </summary>
    private WorkspaceServices BindDefaultWorkspace(string sessionId)
    {
        if (Log.GetHead(sessionId) == 0)
        {
            var root = DefaultWorkspace.Root;
            RefuseRemoving(root);
            try
            {
                Log.Append(sessionId, 0, [EventData.Create(new WorkspaceBound(root))]);
                _sessionWorkspaces[sessionId] = new SessionWorkspace(root);
                return DefaultWorkspace;
            }
            catch (EventLogConflictException)
            {
                // Written by another surface meanwhile: whatever it recorded stands.
            }
        }
        return WorkspaceServicesOf(sessionId);
    }

    /// <summary>
    /// The <see cref="WorkspaceBound"/> a child of <paramref name="parentId"/> starts with: its parent's, or the configured
    /// workspace for a parent whose log (written before A6) recorded none.
    /// </summary>
    private WorkspaceBound ChildBinding(string parentId) =>
        WorkspaceOf(parentId) is var parent && !parent.IsDefault
            ? new WorkspaceBound(parent.Path, parent.Repository, parent.Branch)
            : new WorkspaceBound(DefaultWorkspace.Root);

    // A6 review (M2): worktrees being removed (RemoveWorktreeAsync), from before it asks until git is done: no session
    // starts, resumes or builds services in one meanwhile.
    private readonly ConcurrentDictionary<string, byte> _removing = new(StringComparer.Ordinal);

    private void RefuseRemoving(string root)
    {
        foreach (var removing in _removing.Keys)
            if (root == removing || root.StartsWith(removing + "/", StringComparison.Ordinal))
                throw new WorkspaceUnavailableException($"The worktree {TerminalText.Visible(removing)} is being removed; Corvid will not work in it.");
    }

    private static string Canonical(string path)
    {
        try
        {
            return Directory.Exists(path) ? new Workspace(path).Root : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
