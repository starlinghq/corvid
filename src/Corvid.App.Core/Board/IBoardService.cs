namespace Corvid.App.Core.Board;

/// <summary>
/// What the Board needs beyond the sessions (which come from <see cref="Sessions.ISessionService"/> and the
/// <see cref="Sessions.SessionHub"/>): the attention items the other areas hold (held MCP tools, pairing codes, staged
/// memory, finished jobs, Vault confirms) and the engine's state for the status bar. The demo implements it now;
/// Corvid.App.Engine will over the MCP pins, the gateway, the memory store and the scheduler.
/// </summary>
public interface IBoardService
{
    /// <summary>The areas' attention items, oldest first.</summary>
    Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(CancellationToken cancellationToken = default);

    /// <summary>An item arrived or went away. Raised on the UI thread.</summary>
    event EventHandler? AttentionChanged;

    /// <summary>
    /// Answers an item with one of its <see cref="AttentionActionKind.Resolve"/> actions (ignore a pairing code). The item
    /// leaves the list and <see cref="AttentionChanged"/> is raised; an unknown item or action is ignored. A refused answer
    /// throws, and the item stays.
    /// </summary>
    Task ResolveAsync(string itemId, string actionId, CancellationToken cancellationToken = default);

    /// <summary>The engine's state for the status bar.</summary>
    Task<BoardStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
