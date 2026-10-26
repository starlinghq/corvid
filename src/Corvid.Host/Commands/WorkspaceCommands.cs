using Corvid.Host.Composition;
using Corvid.Kernel.Workspaces;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/workspace</c> (A6, G-4): where this session works, and sessions elsewhere. A session keeps the workspace it was
/// created in, so <c>open</c> and <c>new</c> start another session (let go of at once, as <c>/fork</c>'s is, for
/// <c>corvid chat --session</c> or the app to drive). <c>new</c> and <c>remove</c> make and remove git worktrees of this
/// session's repository, each after the person approves it here, since it writes outside the workspace.
/// </summary>
internal static class WorkspaceCommands
{
    public const string Usage = "[list | open <path> | new [<name>] | remove <name>]";

    public static async ValueTask<CommandOutput> RunAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var runtime = context.Runtime;
        var session = context.Session;
        try
        {
            switch (invocation.Arguments)
            {
                case []:
                    return Show(runtime, session);
                case ["list"]:
                    return await ListAsync(runtime, session, cancellationToken).ConfigureAwait(false);
                case ["open", ..]:
                    var path = invocation.Text.Trim()["open".Length..].Trim();
                    if (path.Length == 0)
                        return CommandOutput.Error("Usage: /workspace open <path>.");
                    var opened = runtime.OpenSession(surface: session.Surface, settings: new SessionSettings { Workspace = CorvidPathsOrSelf(path) });
                    return await HandOverAsync(runtime, opened, $"Started {opened.Id} in {TerminalText.Visible(opened.Workspace.Root)}.").ConfigureAwait(false);
                case ["new"] or ["new", _]:
                    var name = invocation.Arguments.Length == 2 ? invocation.Arguments[1] : null;
                    var created = await runtime.OpenWorktreeSessionAsync(session.Id, name, session.Surface, cancellationToken).ConfigureAwait(false);
                    var bound = runtime.WorkspaceOf(created.Id);
                    return await HandOverAsync(runtime, created, $"Made the worktree {TerminalText.Visible(created.Workspace.Root)} on the branch " +
                        $"{TerminalText.Visible(bound.Branch ?? "")}, and started {created.Id} in it.").ConfigureAwait(false);
                case ["remove", var worktree]:
                    var removed = await runtime.RemoveWorktreeAsync(session.Id, worktree, cancellationToken).ConfigureAwait(false);
                    return CommandOutput.Success($"Removed the worktree {TerminalText.Visible(removed.Path)}; its branch " +
                        $"{TerminalText.Visible(removed.Branch ?? "(detached)")} stays.");
                default:
                    return CommandOutput.Error("Usage: /workspace " + Usage + ".");
            }
        }
        catch (Exception exception) when (exception is WorkspaceUnavailableException or WorktreeException or WorktreeRefusedException)
        {
            return CommandOutput.Error(TerminalText.Visible(exception.Message));
        }
    }

    // A leading ~/ is the home directory, as in configuration.
    private static string CorvidPathsOrSelf(string path) => Configuration.CorvidPaths.ExpandHome(path);

    private static CommandOutput Show(CorvidRuntime runtime, CorvidSession session)
    {
        var bound = runtime.WorkspaceOf(session.Id);
        var lines = new List<string>
        {
            $"This session works in {TerminalText.Visible(session.Workspace.Root)}" + (bound.IsDefault ? " (the configured workspace)." : "."),
        };
        if (bound.Branch is { } branch)
            lines.Add($"It is a worktree of {TerminalText.Visible(bound.Repository ?? "")} on the branch {TerminalText.Visible(branch)}.");
        var others = runtime.OpenWorkspaces.Where(workspace => workspace.Root != session.Workspace.Root).ToList();
        if (others.Count > 0)
            lines.Add("Other workspaces open here: " + string.Join(", ", others.Select(workspace => TerminalText.Visible(workspace.Root))) + ".");
        lines.Add("A session keeps its workspace: /workspace open <path> or /workspace new [<name>] (a git worktree) starts another; /workspace list shows the worktrees Corvid made, and /workspace remove <name> removes one.");
        return new CommandOutput { Title = "Workspace", Lines = [.. lines] };
    }

    private static async Task<CommandOutput> ListAsync(CorvidRuntime runtime, CorvidSession session, CancellationToken cancellationToken)
    {
        var repository = await runtime.RepositoryOfAsync(session.Id).ConfigureAwait(false);
        var worktrees = await runtime.ListWorktreesAsync(session.Id, cancellationToken).ConfigureAwait(false);
        if (worktrees.IsEmpty)
            return CommandOutput.Text($"Corvid has made no worktrees of {TerminalText.Visible(repository.MainCheckout)}. /workspace new [<name>] makes one.");
        return new CommandOutput
        {
            Title = "Worktrees of " + TerminalText.Visible(repository.MainCheckout),
            Table = CommandTable.Of(["name", "branch", "path", "sessions"], worktrees.Select(item => new[]
            {
                TerminalText.Visible(item.Worktree.Name), TerminalText.Visible(item.Worktree.Branch ?? "(detached)"),
                TerminalText.Visible(item.Worktree.Path), item.Sessions.IsEmpty ? "none" : string.Join(", ", item.Sessions.Select(TerminalText.Visible)),
            })),
        };
    }

    // As /fork does: the new session runs nowhere here, so it is let go of for another process (or the app) to drive.
    private static async Task<CommandOutput> HandOverAsync(CorvidRuntime runtime, CorvidSession session, string first)
    {
        var released = await runtime.ReleaseDriverAsync(session.Id).ConfigureAwait(false);
        return CommandOutput.Success(first, released
            ? $"Continue it with corvid chat --session {session.Id}, or open it in the app."
            : $"It is open in this process; continue it with corvid chat --session {session.Id} once this one leaves.");
    }
}
