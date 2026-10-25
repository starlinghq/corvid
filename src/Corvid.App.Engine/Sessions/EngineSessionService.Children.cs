using Corvid.Abstractions.Events;
using Corvid.App.Core.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// A5 (plan §14, docs/plan/ui.md §4.13 and F18): child sessions in the window. A parent's view gets its children as they
/// start and end (<see cref="ChildSessionChanged"/>, live from <c>runtime.ChildChanged</c> and on replay); a child's summary
/// names its parent and what it was started with, so the Board shows it under the parent; a child opens read-only with its
/// parent linked, and follows its log while it runs here. Nothing in the window drives a child.
/// </summary>
public sealed partial class EngineSessionService
{
    /// <summary>What drives a child, for its read-only note.</summary>
    internal static string ChildHolder(string parentId) => $"a child session of {parentId}, run by it";

    /// <summary>A child session (<c>child-…</c>): its parent's record, which the window only shows.</summary>
    internal static bool IsChild(string id) => id.StartsWith(ChildSessions.Surface + "-", StringComparison.Ordinal);

    /// <summary>
    /// A5: the window never opens a child as its own session (a message, a mode, a goal, a command): opened again, a finished
    /// child would be an ordinary session with the parent's full toolset.
    /// </summary>
    private static void RefuseChild(string sessionId)
    {
        if (IsChild(sessionId))
            throw new InvalidOperationException($"Session {sessionId} is a child session: read-only here, its parent's record.");
    }

    /// <summary>A child's first event (<c>ChildOf</c>), or null when the session is not one.</summary>
    private static ChildOf? ChildOrigin(CorvidRuntime runtime, string id) =>
        IsChild(id) && runtime.Log.Read(id, 0, limit: 1) is [{ Payload: ChildOf origin }] ? origin : null;

    /// <summary>
    /// A child's summary fields: its parent, what it was started with and how it ended (from the parent's log), read-only.
    /// Null for a session that is not a child.
    /// </summary>
    private static (string ParentId, ChildSessionChanged Child)? ChildFacts(CorvidRuntime runtime, string id)
    {
        if (ChildOrigin(runtime, id) is not { } origin)
            return null;
        var listed = runtime.Children(origin.ParentId).FirstOrDefault(child => child.ChildId == id);
        var child = listed is null
            ? new ChildSessionChanged(id, origin.Goal, "unfinished")
            {
                Tools = origin.Tools.IsDefault ? [] : origin.Tools,
                Budget = new ChildBudgetInfo(origin.Budget.MaxSteps, origin.Budget.MaxTokens, origin.Budget.MaxSeconds),
                Mix = SessionUpdateTranslator.MixText(origin.Mix),
            }
            : listed.Finished is { } finished
                ? SessionUpdateTranslator.Ended(SessionUpdateTranslator.Started(listed.Spawned), finished)
                : SessionUpdateTranslator.Started(listed.Spawned) with { Status = listed.Running ? ChildSessionChanged.Running : "unfinished" };
        return (origin.ParentId, child);
    }

    /// <summary>A5: a child of a session open in the window started or ended: the parent's view and both summaries hear of it.</summary>
    private void OnChildChanged(CorvidRuntime runtime, string parentId, SessionStreamUpdate update)
    {
        if (_disposed)
            return;
        if (_channels.TryGetValue(parentId, out var channel))
            Publish(channel, translator => translator.Child(update));
        var childId = update switch
        {
            ChildStarted started => started.Child.ChildId,
            ChildEnded ended => ended.Child.ChildId,
            _ => null,
        };
        if (childId is not null)
            Refresh(runtime, childId);
        Refresh(runtime, parentId);
    }

    /// <summary>
    /// A5: stops a child session this window's runtime runs (the Board's or the header's Stop). False when it is not running
    /// here. The parent's view hears of its end as for any child.
    /// </summary>
    public async Task<bool> StopChildAsync(string childId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        return runtime.StopChild(childId);
    }
}
