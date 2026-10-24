using System.Collections.Immutable;
using System.Globalization;
using Corvid.App.Core.Review;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Rewind;

/// <summary>
/// Rewind over the demo sessions. The ledger is the session's own (<see cref="SessionView.Ledger"/>, which the Sessions
/// demo seeds as Rewind.png lists it); the plan inverts what every later call changed and a later restore has not already
/// put back; a restore goes on the session's stream as a restore checkpoint and <see cref="SessionRewound"/>, so the
/// transcript drops the turns, the fold marks the files put back, and the marker shows. A restore point's result is read
/// back from that marker. What the demo knows beyond the log: the result of the 13:41 undo of turn 8 (Rewind.png's "Last
/// restore"), and two edits the person made by hand, which make a restore merge (SeccompTests.cs) or refuse
/// (docs/notes.md).
/// </summary>
public sealed class DemoRewindService : IRewindService
{
    private readonly SessionHub _sessions;
    private readonly DemoSessionService _demo;
    /// <summary>Results the log does not carry: the seeded undo of turn 8, by session and restore point.</summary>
    private readonly Dictionary<(string Session, string Checkpoint), RestoreOutcome> _outcomes = [];
    /// <summary>A session's latest refused restore, which takes no checkpoint, and the ledger's length when it was refused.</summary>
    private readonly Dictionary<string, (RestoreOutcome Outcome, int LedgerCount)> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableList<LedgerRow>> _watched = new(StringComparer.Ordinal);
    private readonly ImmutableArray<PersonEdit> _edits;
    private readonly TimeZoneInfo _zone;

    public DemoRewindService(SessionHub sessions, DemoSessionService demo, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentNullException.ThrowIfNull(time);
        _sessions = sessions;
        _demo = demo;
        _zone = time.LocalTimeZone;
        var now = TimeZoneInfo.ConvertTime(demo.Clock, _zone);
        DateTimeOffset At(int hour, int minute) => new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        _outcomes[(DemoSessionService.MainSessionId, "c-35")] = new RestoreOutcome("undo of turn 8", At(13, 41), RewindScope.FilesAndConversation)
        {
            Restored = ["src/Corvid.Cli/Chat/ChatRenderer.cs", "tests/Corvid.Cli.Tests/RendererTests.cs"],
            Merged = ["README.md"],
            CheckpointId = "c-35",
        };
        _edits =
        [
            new PersonEdit(DemoReviewService.EditedByYouPath, At(14, 5), "c-43"),
            new PersonEdit("docs/notes.md", At(13, 35), "c-32"),
        ];
    }

    public event EventHandler<string>? TimelineChanged;

    public Task<RewindTimeline> GetTimelineAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = Session(sessionId);
        var state = session.State;
        var entries = state.Ledger.Reverse().Select(row => Entry(row, state)).ToImmutableArray();
        // A refused restore takes no checkpoint, so the ledger lacks it: it is the latest result unless a restore point
        // (from here, or a file put back from Review) came after it.
        var newest = state.Ledger.FindLastIndex(row => row.Kind == CheckpointKind.Restore);
        var last = _refused.TryGetValue(sessionId, out var refused) && newest < refused.LedgerCount
            ? refused.Outcome
            : entries.FirstOrDefault(entry => entry.Kind == LedgerEntryKind.Restore && entry.Outcome is not null)?.Outcome;
        return Task.FromResult(new RewindTimeline(sessionId) { Entries = entries, LastRestore = last, Turn = state.Turn });
    }

    public Task<RestorePlan> PlanAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Plan(Session(sessionId).State, checkpointId));

    public Task<IReadOnlyList<RestorePreviewFile>> PreviewAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var plan = Plan(Session(sessionId).State, checkpointId);
        IReadOnlyList<RestorePreviewFile> files =
        [
            .. plan.Files.Select(file => DemoChangedFiles.Known.TryGetValue(file.Path, out var known)
                ? new RestorePreviewFile(file.Path, file.Kind) { CurrentText = known.New, RestoredText = known.Old ?? "" }
                : new RestorePreviewFile(file.Path, file.Kind) { Note = "no text in the demo" }),
        ];
        return Task.FromResult(files);
    }

    public Task<RestoreOutcome> RestoreAsync(string sessionId, string checkpointId, RewindScope scope, CancellationToken cancellationToken = default)
    {
        var state = Session(sessionId).State;
        var plan = Plan(state, checkpointId);
        var label = $"rollback to before {checkpointId}";
        var at = _demo.Clock;
        var filesGo = scope != RewindScope.ConversationOnly;
        var conflicts = filesGo
            ? plan.Files.Where(file => file.Kind == PutBackKind.Conflict).Select(file => new RestoreConflict(file.Path, file.Reason ?? "your edit conflicts")).ToImmutableArray()
            : [];
        RestoreOutcome outcome;
        if (!conflicts.IsEmpty)
        {
            outcome = new RestoreOutcome(label, at, scope)
            {
                Conflicts = conflicts,
                Refused = true,
                Detail = string.Join("; ", conflicts.Select(conflict => $"{conflict.Path}: {conflict.Reason}")) + ". Nothing was overwritten.",
            };
            _demo.Rewind(sessionId, null, new SessionRewound(plan.FromTurn, scope, "rollback")
            {
                Conflicts = conflicts,
                Refused = true,
                Detail = outcome.Detail,
            });
            _refused[sessionId] = (outcome, state.Ledger.Count);
            Raise(sessionId);
            return Task.FromResult(outcome);
        }

        var restorePoint = _demo.NextRestorePoint(sessionId);
        outcome = new RestoreOutcome(label, at, scope)
        {
            Restored = filesGo ? [.. plan.Files.Where(file => file.Kind == PutBackKind.Restore).Select(file => file.Path)] : [],
            Merged = filesGo ? [.. plan.Files.Where(file => file.Kind == PutBackKind.Merge).Select(file => file.Path)] : [],
            CheckpointId = restorePoint,
        };
        _demo.Rewind(sessionId,
            new CheckpointRecorded(restorePoint, CheckpointKind.Restore) { Target = label, Turn = state.Turn },
            new SessionRewound(plan.FromTurn, scope, "rollback")
            {
                Restored = outcome.Restored,
                Merged = outcome.Merged,
                CheckpointId = restorePoint,
            });
        Raise(sessionId);
        return Task.FromResult(outcome);
    }

    private RestorePlan Plan(SessionView state, string checkpointId)
    {
        var ledger = state.Ledger;
        var index = ledger.FindIndex(row => row.Kind == CheckpointKind.Restore ? row.Id == checkpointId : row.BeforeId == checkpointId);
        if (index < 0)
            throw new KeyNotFoundException($"No checkpoint {checkpointId} in the ledger of {state.SessionId}.");
        var row = ledger[index];
        if (row.Kind == CheckpointKind.Before && IsPending(row, state))
            throw new InvalidOperationException($"The call at {checkpointId} has not finished; there is nothing of it to go back from yet.");

        // Going back to before a restore point undoes that restore: the paths it put back return to how it found them.
        // The restore's own counts are not in its result, so those paths go back without them.
        var files = new List<(string Path, int Added, int Removed)>();
        if (row.Kind == CheckpointKind.Restore && OutcomeOf(row, state) is { Refused: false } undone)
        {
            foreach (var path in undone.Restored.Concat(undone.Merged))
            {
                if (!files.Exists(file => file.Path == path))
                    files.Add((path, 0, 0));
            }
        }

        // Every file a later call changed goes back, in the order the calls first touched it, unless a later restore has
        // already put that change back; its counts are undone.
        foreach (var later in ledger.Skip(index))
        {
            foreach (var change in later.LiveChanges.Where(change => change.Readable))
            {
                var at = files.FindIndex(file => file.Path == change.Path);
                if (at < 0)
                    files.Add((change.Path, change.Added, change.Removed));
                else
                    files[at] = (change.Path, files[at].Added + change.Added, files[at].Removed + change.Removed);
            }
        }

        var putBack = files.Select(file =>
        {
            var edit = _edits.FirstOrDefault(edit => edit.Path == file.Path);
            if (edit is null)
                return new PutBackFile(file.Path, PutBackKind.Restore, file.Added, file.Removed);
            // The call created docs/notes.md, so going back deletes it, and the person's edit with it: that is refused.
            return string.Equals(edit.Path, "docs/notes.md", StringComparison.Ordinal)
                ? new PutBackFile(file.Path, PutBackKind.Conflict, file.Added, file.Removed)
                {
                    Edit = edit,
                    Reason = $"you edited it at {TimeZoneInfo.ConvertTime(edit.At, _zone):HH:mm}, after {edit.AfterCheckpoint}, and going back to {checkpointId} would delete it",
                }
                : new PutBackFile(file.Path, PutBackKind.Merge, file.Added, file.Removed) { Edit = edit };
        }).ToImmutableArray();

        var calls = ledger.Where(candidate => candidate.Kind != CheckpointKind.Restore).Reverse().ToList();
        var position = row.Kind == CheckpointKind.Restore ? -1 : calls.FindIndex(candidate => candidate.BeforeId == checkpointId);
        // The turns that leave are those from the entry's turn to the session's latest; a turn an earlier restore already
        // took out has lowered the latest, so it is not counted again.
        return new RestorePlan(checkpointId, row.At, row.Turn)
        {
            Files = putBack,
            FromTurn = row.Kind == CheckpointKind.Restore ? row.Turn + 1 : row.Turn,
            ToTurn = state.Turn,
            Command = position < 0 ? null : string.Create(CultureInfo.InvariantCulture, $"/rollback {position + 1}"),
        };
    }

    private LedgerEntry Entry(LedgerRow row, SessionView state) => row.Kind switch
    {
        CheckpointKind.Restore => new LedgerEntry(row.Id, LedgerEntryKind.Restore, row.At, row.Turn)
        {
            Target = row.Target,
            Outcome = OutcomeOf(row, state),
        },
        CheckpointKind.Before when IsPending(row, state) => new LedgerEntry(row.BeforeId ?? row.Id, LedgerEntryKind.Pending, row.At, row.Turn)
        {
            Tool = row.Tool,
            Target = row.Target,
            WaitingOnApproval = row.CallId is not null && state.Approvals.Any(open => open.CallId == row.CallId),
        },
        // A call that ended without an after checkpoint (cancelled, or its turn rewound) changed nothing it recorded.
        CheckpointKind.Before => new LedgerEntry(row.BeforeId ?? row.Id, LedgerEntryKind.Call, row.At, row.Turn)
        {
            Tool = row.Tool,
            Target = row.Target,
            Stopped = true,
        },
        _ => new LedgerEntry(row.BeforeId ?? row.Id, LedgerEntryKind.Call, row.At, row.Turn)
        {
            Tool = row.Tool,
            Target = row.Target,
            AfterId = row.AfterId,
            Changes = row.Changes,
        },
    };

    /// <summary>
    /// A restore point's result: the marker the restore left on the session's stream (<see cref="RewindEntry"/>, which
    /// stays in the transcript whatever turns leave), or the seeded one the log does not carry.
    /// </summary>
    private RestoreOutcome? OutcomeOf(LedgerRow row, SessionView state)
    {
        if (_outcomes.TryGetValue((state.SessionId, row.Id), out var known))
            return known;
        var marker = state.Transcript.OfType<RewindEntry>().LastOrDefault(entry => entry.Rewind.CheckpointId == row.Id)?.Rewind;
        return marker is null
            ? null
            : new RestoreOutcome(row.Target ?? marker.Reason, row.At, marker.Scope)
            {
                Restored = marker.Restored,
                Merged = marker.Merged,
                Conflicts = marker.Conflicts,
                CheckpointId = row.Id,
                Refused = marker.Refused,
                Detail = marker.Detail,
            };
    }

    /// <summary>A call with only its before checkpoint is pending while it runs or waits on an approval.</summary>
    private static bool IsPending(LedgerRow row, SessionView state) =>
        row.CallId is { } callId
        && (state.Approvals.Any(open => open.CallId == callId)
            || state.Transcript.OfType<ToolEntry>().Any(tool => tool.CallId == callId && tool.Status is ToolCallStatus.InProgress or ToolCallStatus.Pending));

    private SessionViewModel Session(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var session = _sessions.Get(sessionId);
        if (_watched.TryAdd(sessionId, session.State.Ledger))
        {
            session.StateChanged += (_, _) =>
            {
                if (session.State.Ledger == _watched[sessionId])
                    return;
                _watched[sessionId] = session.State.Ledger;
                Raise(sessionId);
            };
        }

        return session;
    }

    private void Raise(string sessionId) => TimelineChanged?.Invoke(this, sessionId);
}
