using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Rewind;

/// <summary>
/// Rewind's seam to the engine (docs/plan/ui.md §4.6, F5): a session's checkpoint ledger, the plan and the preview of going
/// back to before an entry, and the restore itself, whose result goes on the session's stream as <c>SessionRewound</c>
/// so the transcript shows the marker. The demo implements it over the demo sessions; <c>Corvid.App.Engine</c> will over
/// the runtime's ledger and rollback.
/// </summary>
public interface IRewindService
{
    /// <summary>A session's ledger changed (a checkpoint was taken, a restore ran); raised on the UI thread with its id.</summary>
    event EventHandler<string>? TimelineChanged;

    Task<RewindTimeline> GetTimelineAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>What going back to before the checkpoint would put back, and which turns would leave.</summary>
    Task<RestorePlan> PlanAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default);

    /// <summary>The diff of each file the restore would change, from the file now to the file restored.</summary>
    Task<IReadOnlyList<RestorePreviewFile>> PreviewAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Goes back to before the checkpoint: files, conversation or both. A conflict with the person's own edit refuses
    /// the whole restore and changes nothing; the outcome says which path and why.
    /// </summary>
    Task<RestoreOutcome> RestoreAsync(string sessionId, string checkpointId, RewindScope scope, CancellationToken cancellationToken = default);
}
