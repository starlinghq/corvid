using Corvid.App.Core.Sessions;
using Corvid.Host.Composition;
using Corvid.Kernel.Workspaces;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// A6 (G-4): sessions in other workspaces and in new git worktrees, from the window. A session keeps the workspace it was
/// started in; its approvals, sandbox, file tools, checkpoints, Review and Rewind follow it (the host's
/// <see cref="WorkspaceServices"/>). Making or removing a worktree writes outside the workspace, so the host asks the
/// person first, through the asking session's approvals: here, its card on that session's page, also outside a turn.
/// </summary>
public sealed partial class EngineSessionService
{
    public bool CanChooseWorkspace => true;

    /// <summary>
    /// A new, empty session in the workspace <paramref name="request"/> names, or in a new git worktree of the asking
    /// session's workspace once the person approves it on that session's card. Refusals (a directory Corvid will not work
    /// in, a worktree denied or impossible) throw <see cref="InvalidOperationException"/> with why.
    /// </summary>
    public async Task<string> CreateAsync(NewSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is { Workspace: null, Worktree: false })
            return await CreateAsync(cancellationToken).ConfigureAwait(false);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        string id;
        try
        {
            if (request.Worktree)
            {
                if (request.AskIn is not { Length: > 0 } askIn)
                    throw new InvalidOperationException("A worktree is made of an open session's workspace, after you approve it there: open a session first.");
                var channel = await LoadAsync(askIn, cancellationToken).ConfigureAwait(false);
                Writable(channel);
                await SessionAsync(runtime, askIn, cancellationToken).ConfigureAwait(false);
                using var cards = await AttachCardsAsync(runtime, channel).ConfigureAwait(false);
                // Asking blocks until the person answers the card: off the UI thread, as git is.
                id = await Task.Run(async () => (await runtime.OpenWorktreeSessionAsync(askIn, request.WorktreeName, Surface, cancellationToken)
                    .ConfigureAwait(false)).Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A leading ~/ is the home directory, as in configuration.
                var workspace = Corvid.Host.Configuration.CorvidPaths.ExpandHome(request.Workspace!.Trim());
                // The configured workspace is the window's own: a session there records nothing more, as before A6.
                if (await Task.Run(() => runtime.CheckWorkspace(workspace), cancellationToken).ConfigureAwait(false) == runtime.DefaultWorkspace.Root)
                    return await CreateAsync(cancellationToken).ConfigureAwait(false);
                // Opening a session writes to the log and may build the workspace's services: off the UI thread.
                id = await Task.Run(() => runtime.OpenSession(surface: Surface, settings: new SessionSettings { Workspace = workspace }).Id,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WorktreeException or WorktreeRefusedException)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        _created[id] = _time.GetUtcNow();
        Refresh(runtime, id);
        return id;
    }

    /// <summary>
    /// The window's cards answer the session's approvals while nothing else does: a command run outside a turn (making a
    /// worktree). Null while a turn of the window runs or starts (its own prompter answers them), so the turn's is never
    /// replaced. Disposing it detaches only this one.
    /// </summary>
    private async Task<IDisposable?> AttachCardsAsync(Corvid.Host.Composition.CorvidRuntime runtime, Channel channel)
    {
        lock (channel.Gate)
            if (channel.Running || channel.Prompter is not null)
                return null;
        var tools = await _engine.ToolsAsync().ConfigureAwait(false);
        var scoped = await Task.Run(() => tools.ForSessionOrSelf(channel.SessionId)).ConfigureAwait(false);
        return runtime.AttachPrompter(channel.SessionId, new CardPrompter(this, channel, runtime, scoped, reporter: null));
    }
}
