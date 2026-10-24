using System.Collections.Immutable;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Review;

/// <summary>
/// Review over the demo sessions: the changed files are the session's own (<see cref="SessionView.TurnChanges"/> and
/// <see cref="SessionView.SessionChanges"/>), their texts come from the demo's files, the checkpoint, call and turn of each
/// from the session's write ledger, and comments go to the session through <see cref="ISessionService.SendAsync"/>, where
/// they start a turn or wait in the queue. Putting a file back goes on the session's stream as a restore does (a restore
/// checkpoint and a files-only <see cref="SessionRewound"/>), so Rewind lists it and the change lists drop it. The review
/// point of Review.png (turn 11, two comments sent at 14:02) is seeded for <c>cli-9f2e</c>; points live here, as G-7 keeps
/// them in <c>ui.db</c> beside the log rather than in it.
/// </summary>
public sealed class DemoReviewService : IReviewService
{
    /// <summary>The demo's main session, whose review point and file history Review.png draws.</summary>
    public const string MainSessionId = "cli-9f2e";

    /// <summary>The person's own edit the demo knows of: SeccompTests.cs at 14:05, after c-43 (Rewind.png says so too).</summary>
    public const string EditedByYouPath = DemoFiles.SeccompTestsPath;

    private readonly SessionHub _sessions;
    private readonly DemoSessionService _demo;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ReviewPoint> _points = new(StringComparer.Ordinal);
    /// <summary>
    /// Where a send put the review point: the ledger's finished calls at that moment, by their after checkpoints. "Since
    /// last review" is every finished call not among them, so a call that was running at the send counts once it ends.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _reviewed = new(StringComparer.Ordinal);
    /// <summary>Files put back from here: the checkpoint and the turn of the call it went back from, by session and path.</summary>
    private readonly Dictionary<string, Dictionary<string, (string Checkpoint, int Turn)>> _reverted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ImmutableArray<FileChange> Turn, ImmutableArray<FileChange> Session, ImmutableList<LedgerRow> Ledger)> _watched = new(StringComparer.Ordinal);

    public DemoReviewService(SessionHub sessions, DemoSessionService demo, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(time);
        _sessions = sessions;
        _demo = demo;
        _time = time;
        var today = time.GetLocalNow();
        _points[MainSessionId] = new ReviewPoint(11, 2, new DateTimeOffset(today.Year, today.Month, today.Day, 14, 2, 0, today.Offset));
    }

    public event EventHandler<string>? ChangesChanged;

    public Task<ReviewChanges> GetChangesAsync(string sessionId, ReviewScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = _sessions.Get(sessionId);
        Watch(session);
        var state = session.State;
        _points.TryGetValue(sessionId, out var point);
        _reviewed.TryGetValue(sessionId, out var reviewed);
        // The ledger's calls the scope covers: what a file's checkpoint, call and turn are taken from.
        Func<LedgerRow, bool> inScope = scope switch
        {
            ReviewScope.ThisTurn => row => row.Turn == state.Turn,
            ReviewScope.SinceLastReview when reviewed is not null => row => !reviewed.Contains(row.AfterId ?? ""),
            _ => _ => true,
        };
        var changes = scope switch
        {
            ReviewScope.ThisTurn => state.TurnChanges,
            ReviewScope.ThisSession => state.SessionChanges,
            // Until comments are sent from here, the seeded point stands for the session's list (Review.png); after a
            // send, what the ledger's calls changed after it.
            _ when reviewed is null => state.SessionChanges,
            _ => Since(state, inScope),
        };
        var rows = state.Ledger.Where(row => row.Kind == CheckpointKind.After && inScope(row)).ToList();
        var files = (changes.IsDefault ? [] : changes).Select(change => File(change, rows)).ToList();
        // A file put back from here that no longer counts as changed stays listed, as reverted, until it changes again.
        if (_reverted.TryGetValue(sessionId, out var reverted))
        {
            foreach (var (path, (checkpoint, turn)) in reverted)
            {
                if (files.Exists(file => file.Path == path) || scope == ReviewScope.ThisTurn && turn != state.Turn)
                    continue;
                files.Add(new ReviewFile(path, 0, 0) { Reverted = true, CheckpointId = checkpoint });
            }
        }

        var checkpoints = files.Where(file => !file.Reverted).Select(file => file.CheckpointId).OfType<string>().OrderBy(Number).ToList();
        return Task.FromResult(new ReviewChanges(sessionId, scope)
        {
            Files = [.. files],
            Turn = state.Turn,
            ReviewPoint = point,
            FirstCheckpoint = checkpoints.FirstOrDefault(),
            LastCheckpoint = checkpoints.LastOrDefault(),
        });
    }

    public async Task<ReviewPoint> SendCommentsAsync(string sessionId, IReadOnlyList<ReviewComment> comments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count == 0)
            throw new ArgumentException("There are no comments to send.", nameof(comments));
        var state = _sessions.Get(sessionId).State;
        var reviewed = state.Ledger.Where(row => row.Kind == CheckpointKind.After).Select(row => row.AfterId ?? "").ToHashSet(StringComparer.Ordinal);
        await _sessions.Service.SendAsync(sessionId, ReviewMessage.Compose(comments), cancellationToken).ConfigureAwait(true);
        var point = new ReviewPoint(state.Turn, comments.Count, _time.GetLocalNow());
        _points[sessionId] = point;
        _reviewed[sessionId] = reviewed;
        return point;
    }

    public Task<FileRevertResult> RevertFileAsync(string sessionId, string path, string checkpointId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        var state = _sessions.Get(sessionId).State;
        var call = state.Ledger.FirstOrDefault(row => row.Kind == CheckpointKind.After && row.BeforeId == checkpointId);
        if (call is null || !state.Ledger.Any(row => row.Kind == CheckpointKind.After && row.Turn >= call.Turn && row.LiveChanges.Any(change => change.Path == path)))
            return Task.FromResult(new FileRevertResult(path, checkpointId, false, true, $"no call since {checkpointId} changed it"));

        // The person edited SeccompTests.cs after the checkpoint: the restore merges three ways and keeps that edit.
        var merged = string.Equals(path, EditedByYouPath, StringComparison.Ordinal);
        // Known before the stream moves, so the lists Review reloads as it hears the restore already show it reverted.
        if (!_reverted.TryGetValue(sessionId, out var reverted))
            _reverted[sessionId] = reverted = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        reverted[path] = (checkpointId, call.Turn);
        var restorePoint = _demo.NextRestorePoint(sessionId);
        _demo.Rewind(sessionId,
            new CheckpointRecorded(restorePoint, CheckpointKind.Restore) { Target = $"revert {System.IO.Path.GetFileName(path)} to {checkpointId}", Turn = state.Turn },
            new SessionRewound(call.Turn, RewindScope.FilesOnly, "revert")
            {
                Restored = merged ? [] : [path],
                Merged = merged ? [path] : [],
                CheckpointId = restorePoint,
            },
            stopRunningTurn: false);
        return Task.FromResult(new FileRevertResult(path, checkpointId, merged, false));
    }

    public Task<string> OpenInEditorAsync(string path, int line, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // The demo runs nothing on the person's machine; it says what the engine would run.
        return Task.FromResult(FormattableString.Invariant($"code --goto {path}:{Math.Max(1, line)}"));
    }

    /// <summary>Hears a session's state and tells Review when its lists of changed files are new ones, not on every chunk.</summary>
    private void Watch(SessionViewModel session)
    {
        if (!_watched.TryAdd(session.SessionId, (session.State.TurnChanges, session.State.SessionChanges, session.State.Ledger)))
            return;
        session.StateChanged += (_, _) =>
        {
            var now = (session.State.TurnChanges, session.State.SessionChanges, session.State.Ledger);
            var before = _watched[session.SessionId];
            if (now.TurnChanges == before.Turn && now.SessionChanges == before.Session && now.Ledger == before.Ledger)
                return;
            _watched[session.SessionId] = now;
            ChangesChanged?.Invoke(this, session.SessionId);
        };
    }

    /// <summary>What the ledger's finished calls in range still have in the files, each path's counts summed, in the order first changed.</summary>
    private static ImmutableArray<FileChange> Since(SessionView state, Func<LedgerRow, bool> inScope)
    {
        var files = new List<FileChange>();
        foreach (var row in state.Ledger.Where(row => row.Kind == CheckpointKind.After && inScope(row)))
        {
            foreach (var change in row.LiveChanges)
            {
                var index = files.FindIndex(file => file.Path == change.Path);
                if (index < 0)
                    files.Add(change);
                else
                    files[index] = files[index] with { Added = files[index].Added + change.Added, Removed = files[index].Removed + change.Removed };
            }
        }

        return [.. files];
    }

    /// <summary>
    /// A changed file with its texts, and from the ledger's calls in scope that still have a change of it in the files: the
    /// checkpoint before the first (what the whole file goes back to) and the tool and turn of the last (what changed it
    /// last). None of them when no recorded call covers it, and then it is not reversible from here.
    /// </summary>
    private static ReviewFile File(FileChange change, List<LedgerRow> rows)
    {
        var file = new ReviewFile(change.Path, change.Added, change.Removed)
        {
            Readable = change.Readable,
            Note = change.Readable ? null : "not readable by Corvid",
        };
        if (!change.Readable)
            return file;
        var covering = rows.Where(row => row.LiveChanges.Any(live => live.Path == change.Path)).ToList();
        if (covering.Count > 0)
            file = file with { CheckpointId = covering[0].BeforeId, Tool = covering[^1].Tool, Turn = covering[^1].Turn };
        if (!DemoChangedFiles.Known.TryGetValue(change.Path, out var known))
            return file with { Note = "no text in the demo" };
        return file with { OldText = known.Old, NewText = known.New };
    }

    private static int Number(string checkpointId) =>
        int.TryParse(checkpointId.AsSpan(checkpointId.LastIndexOf('-') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
}
