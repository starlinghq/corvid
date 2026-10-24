using System.Collections.Immutable;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Review;

/// <summary>
/// Review over the demo sessions: the changed files are the session's own (<see cref="SessionView.TurnChanges"/> and
/// <see cref="SessionView.SessionChanges"/>), their texts come from the demo's files, and comments go to the session
/// through <see cref="ISessionService.SendAsync"/>, where they start a turn or wait in the queue. The review point of
/// Review.png (turn 11, two comments sent at 14:02) is seeded for <c>cli-9f2e</c>; points live here, as G-7 keeps them in
/// <c>ui.db</c> beside the log rather than in it.
/// </summary>
public sealed class DemoReviewService : IReviewService
{
    /// <summary>The demo's main session, whose review point and file history Review.png draws.</summary>
    public const string MainSessionId = "cli-9f2e";

    /// <summary>The person's own edit the demo knows of: SeccompTests.cs at 14:05, after c-43 (Rewind.png says so too).</summary>
    public const string EditedByYouPath = DemoFiles.SeccompTestsPath;

    private readonly SessionHub _sessions;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, ReviewPoint> _points = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sentFrom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _reverted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ImmutableArray<FileChange> Turn, ImmutableArray<FileChange> Session)> _watched = new(StringComparer.Ordinal);

    public DemoReviewService(SessionHub sessions, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(time);
        _sessions = sessions;
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
        var changes = scope switch
        {
            ReviewScope.ThisTurn => state.TurnChanges,
            ReviewScope.ThisSession => state.SessionChanges,
            // Until comments are sent from here, the seeded point stands for the session's list (Review.png); after a
            // send, only turns after the point count.
            _ when !_sentFrom.Contains(sessionId) => state.SessionChanges,
            _ when point is not null && state.Turn > point.Turn => state.TurnChanges,
            _ => [],
        };
        _reverted.TryGetValue(sessionId, out var reverted);
        var files = changes.IsDefault ? [] : changes.Select(change => File(change, reverted)).ToImmutableArray();
        var checkpoints = files.Select(file => file.CheckpointId).OfType<string>().OrderBy(Number).ToList();
        return Task.FromResult(new ReviewChanges(sessionId, scope)
        {
            Files = files,
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
        var turn = _sessions.Get(sessionId).State.Turn;
        await _sessions.Service.SendAsync(sessionId, ReviewMessage.Compose(comments), cancellationToken).ConfigureAwait(true);
        var point = new ReviewPoint(turn, comments.Count, _time.GetLocalNow());
        _points[sessionId] = point;
        _sentFrom.Add(sessionId);
        return point;
    }

    public Task<FileRevertResult> RevertFileAsync(string sessionId, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!DemoChangedFiles.Known.TryGetValue(path, out var known))
            return Task.FromResult(new FileRevertResult(path, "", false, true, "no checkpoint covers this file"));
        if (!_reverted.TryGetValue(sessionId, out var reverted))
            _reverted[sessionId] = reverted = new HashSet<string>(StringComparer.Ordinal);
        reverted.Add(path);
        // The person edited SeccompTests.cs after the checkpoint: the restore merges three ways and keeps that edit.
        var merged = string.Equals(path, EditedByYouPath, StringComparison.Ordinal);
        return Task.FromResult(new FileRevertResult(path, known.Checkpoint, merged, false));
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
        if (!_watched.TryAdd(session.SessionId, (session.State.TurnChanges, session.State.SessionChanges)))
            return;
        session.StateChanged += (_, _) =>
        {
            var now = (session.State.TurnChanges, session.State.SessionChanges);
            var before = _watched[session.SessionId];
            if (now.TurnChanges == before.Turn && now.SessionChanges == before.Session)
                return;
            _watched[session.SessionId] = now;
            ChangesChanged?.Invoke(this, session.SessionId);
        };
    }

    private static ReviewFile File(FileChange change, HashSet<string>? reverted)
    {
        var file = new ReviewFile(change.Path, change.Added, change.Removed)
        {
            Readable = change.Readable,
            Note = change.Readable ? null : "not readable by Corvid",
            Reverted = reverted?.Contains(change.Path) == true,
        };
        if (!change.Readable)
            return file;
        if (!DemoChangedFiles.Known.TryGetValue(change.Path, out var known))
            return file with { Note = "no text in the demo" };
        return file with
        {
            OldText = known.Old,
            NewText = known.New,
            Tool = known.Tool,
            Turn = known.Turn,
            CheckpointId = known.Checkpoint,
        };
    }

    private static int Number(string checkpointId) =>
        int.TryParse(checkpointId.AsSpan(checkpointId.LastIndexOf('-') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
}
