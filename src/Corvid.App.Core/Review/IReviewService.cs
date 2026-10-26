namespace Corvid.App.Core.Review;

/// <summary>
/// Review's seam to the engine (docs/plan/ui.md §4.7, F4): the changed files of a session with their texts, the review
/// point, sending line comments as one message, and putting one file back to its checkpoint (G-3). The demo implements it
/// over the demo sessions now; <c>Corvid.App.Engine</c> will over the write ledger and <c>ui.db</c>.
/// </summary>
public interface IReviewService
{
    /// <summary>A session's changed files moved (a call wrote a file); raised on the UI thread with the session's id.</summary>
    event EventHandler<string>? ChangesChanged;

    /// <summary>The files the scope covers, newest change last, with what each diff is drawn from.</summary>
    Task<ReviewChanges> GetChangesAsync(string sessionId, ReviewScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the comments to the session as one message quoting file and line (queued if a turn runs), and moves the
    /// review point to the session's current turn; returns the new point.
    /// </summary>
    Task<ReviewPoint> SendCommentsAsync(string sessionId, IReadOnlyList<ReviewComment> comments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts one file back to the checkpoint Review lists for it (<see cref="ReviewFile.CheckpointId"/>), merging three
    /// ways with the person's own edits (G-3). It goes on the session's stream as a restore does, so Rewind lists it and
    /// the session's change lists drop what went back.
    /// </summary>
    Task<FileRevertResult> RevertFileAsync(string sessionId, string path, string checkpointId, CancellationToken cancellationToken = default);

    /// <summary>Opens the file at a line in the person's editor (<c>$EDITOR</c>, <c>xdg-open</c> or <c>code --goto</c>); returns what ran.</summary>
    Task<string> OpenInEditorAsync(string path, int line, CancellationToken cancellationToken = default);

    /// <summary>
    /// A6 (G-4): <see cref="OpenInEditorAsync(string, int, CancellationToken)"/> for a path of a session's changes, resolved in
    /// the workspace that session works in. A default member: a service with one workspace resolves it there.
    /// </summary>
    Task<string> OpenInEditorAsync(string sessionId, string path, int line, CancellationToken cancellationToken = default) =>
        OpenInEditorAsync(path, line, cancellationToken);
}
