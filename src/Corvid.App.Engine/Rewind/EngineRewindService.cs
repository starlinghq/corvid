using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Rewind;
using Corvid.App.Engine.History;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Events;
using AppConflict = Corvid.App.Core.Sessions.RestoreConflict;
using AppLedgerEntry = Corvid.App.Core.Rewind.LedgerEntry;
using AppPlan = Corvid.App.Core.Rewind.RestorePlan;
using AppRewound = Corvid.App.Core.Sessions.SessionRewound;
using AppScope = Corvid.App.Core.Sessions.RewindScope;
using KernelEntry = Corvid.Kernel.Checkpoints.LedgerEntry;

namespace Corvid.App.Engine.Rewind;

/// <summary>
/// Rewind over the runtime's write ledger (docs/plan/ui.md §4.6, F5): the timeline is the ledger <c>/rollback</c> lists,
/// newest first, with each restore point the log has; the plan and the preview are the kernel's own dry run of the restore
/// (<see cref="CheckpointService.DraftAsync"/>), so the files it says it puts back, merges or stops on are the ones the
/// restore would; the restore is <see cref="CorvidSession.RollbackToAsync"/> with the chosen scope, on the session's actor.
/// <para>
/// What the log does not keep is kept here for this process only: a restore's merged paths and a refused restore (which
/// takes no checkpoint). Going back to before a restore point is refused: the kernel reverts tool calls, not restores.
/// A restore reaches the open transcript only when the session is opened again (the Sessions page's channel replays once).
/// </para>
/// </summary>
public sealed class EngineRewindService : IRewindService, IDisposable
{
    private readonly AppEngine _engine;
    private readonly TimeProvider _time;
    private readonly HistorySignals _signals;
    private readonly ConcurrentDictionary<(string Session, string Checkpoint), RestoreOutcome> _outcomes = new();
    /// <summary>A session's latest restore from here that took no checkpoint (refused, or conversation only), and the head after it.</summary>
    private readonly ConcurrentDictionary<string, (RestoreOutcome Outcome, long Head)> _unrecorded = new(StringComparer.Ordinal);
    private readonly Lazy<Task<LedgerTexts>> _texts;

    internal EngineRewindService(AppEngine engine, TimeProvider time, HistorySignals signals)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(signals);
        _engine = engine;
        _time = time;
        _signals = signals;
        _signals.Changed += OnChanged;
        _texts = new Lazy<Task<LedgerTexts>>(async () => new LedgerTexts(await engine.RuntimeAsync().ConfigureAwait(false)));
    }

    public event EventHandler<string>? TimelineChanged;

    public async Task<RewindTimeline> GetTimelineAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var texts = await _texts.Value.ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            var ledger = SessionLedger.Load(runtime, sessionId, cancellationToken);
            var open = runtime.TryGetSession(sessionId, out _);
            var rows = new List<(long Sequence, AppLedgerEntry Entry)>();
            foreach (var entry in ledger.Entries)
                rows.Add((entry.CallSequence, await EntryAsync(entry, ledger, texts, open).ConfigureAwait(false)));
            var rewinds = ledger.Rewinds.ToList();
            foreach (var restore in ledger.Restores)
            {
                var outcome = OutcomeOf(sessionId, restore, rewinds, ledger);
                rows.Add((restore.Sequence, new AppLedgerEntry(restore.Checkpoint.CheckpointId, LedgerEntryKind.Restore, ledger.TimeOf(restore.Sequence),
                    ledger.TurnAt(restore.Sequence))
                {
                    Target = outcome.Label,
                    Outcome = outcome,
                }));
            }

            return new RewindTimeline(sessionId)
            {
                Entries = [.. rows.OrderByDescending(row => row.Sequence).Select(row => row.Entry)],
                LastRestore = LastRestore(sessionId, ledger, rewinds),
                Turn = ledger.Turn,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppPlan> PlanAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var (ledger, entry, session) = await FindAsync(sessionId, checkpointId, cancellationToken).ConfigureAwait(false);
        var texts = await _texts.Value.ConfigureAwait(false);
        var plan = session.PlanRollbackTo(checkpointId) ?? throw Missing(sessionId, checkpointId);
        var draft = await session.DraftRollbackToAsync(checkpointId, null, cancellationToken).ConfigureAwait(false);

        // Each path the plan's calls changed, in the order they first changed it, with what they changed summed.
        var files = new List<(string Path, int Added, int Removed, string LastAfter)>();
        foreach (var call in plan.Entries)
        {
            foreach (var change in await texts.ChangesAsync(call).ConfigureAwait(false))
            {
                var index = files.FindIndex(file => file.Path == change.Path);
                if (index < 0)
                    files.Add((change.Path, change.Added, change.Removed, call.After!.Checkpoint.CheckpointId));
                else
                    files[index] = (change.Path, files[index].Added + change.Added, files[index].Removed + change.Removed, call.After!.Checkpoint.CheckpointId);
            }
        }

        var putBack = ImmutableArray.CreateBuilder<PutBackFile>();
        foreach (var file in files)
        {
            if (draft is null || draft.Detail is not null)
            {
                // The files cannot go back at all (checkpoints off or unavailable); the conversation still can.
                putBack.Add(new PutBackFile(file.Path, PutBackKind.Conflict, file.Added, file.Removed)
                {
                    Reason = draft?.Detail ?? "checkpoints are off, so no file can be put back",
                });
                continue;
            }
            if (draft.Conflicts.FirstOrDefault(conflict => conflict.Path == file.Path) is { } conflict)
            {
                putBack.Add(new PutBackFile(file.Path, PutBackKind.Conflict, file.Added, file.Removed) { Reason = conflict.Reason });
                continue;
            }
            if (draft.Files.FirstOrDefault(drafted => drafted.Path == file.Path) is not { } drafted)
                continue; // Already as it was: nothing of it to put back.
            putBack.Add(drafted.Merged
                ? new PutBackFile(file.Path, PutBackKind.Merge, file.Added, file.Removed)
                {
                    Edit = texts.Modified(file.Path) is { } at ? new PersonEdit(file.Path, at, file.LastAfter) : null,
                }
                : new PutBackFile(file.Path, PutBackKind.Restore, file.Added, file.Removed));
        }

        var number = ledger.Entries.Length - ledger.Entries.IndexOf(entry);
        return new AppPlan(checkpointId, ledger.TimeOf(entry.Before.Sequence), ledger.TurnAt(entry.CallSequence))
        {
            Files = putBack.ToImmutable(),
            FromTurn = ledger.TurnAt(plan.Position) + 1,
            ToTurn = ledger.Turn,
            PartTurn = ledger.TurnAt(entry.CallSequence) is var turn && turn == ledger.TurnAt(plan.Position) && turn > 0 ? turn : null,
            Command = string.Create(CultureInfo.InvariantCulture, $"/rollback {number}"),
        };
    }

    public async Task<IReadOnlyList<RestorePreviewFile>> PreviewAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default)
    {
        var (ledger, entry, session) = await FindAsync(sessionId, checkpointId, cancellationToken).ConfigureAwait(false);
        var texts = await _texts.Value.ConfigureAwait(false);
        var plan = session.PlanRollbackTo(checkpointId) ?? throw Missing(sessionId, checkpointId);
        var draft = await session.DraftRollbackToAsync(checkpointId, null, cancellationToken).ConfigureAwait(false);
        if (draft is null || draft.Detail is not null)
        {
            var reason = draft?.Detail ?? "checkpoints are off, so no file can be put back";
            return [.. plan.Entries.SelectMany(call => call.Paths).Distinct(StringComparer.Ordinal)
                .Select(path => new RestorePreviewFile(path, PutBackKind.Conflict) { Note = reason })];
        }

        var files = new List<RestorePreviewFile>();
        foreach (var conflict in draft.Conflicts)
            files.Add(new RestorePreviewFile(conflict.Path, PutBackKind.Conflict) { Note = conflict.Reason });
        foreach (var drafted in draft.Files)
        {
            var kind = drafted.Merged ? PutBackKind.Merge : PutBackKind.Restore;
            if (!texts.Readable(drafted.Path))
            {
                files.Add(new RestorePreviewFile(drafted.Path, kind) { Note = "not readable by Corvid" });
                continue;
            }
            var now = await texts.BlobAsync(drafted.Now).ConfigureAwait(false);
            var restored = await texts.BlobAsync(drafted.Restored).ConfigureAwait(false);
            var note = now.Note ?? restored.Note;
            files.Add(note is not null
                ? new RestorePreviewFile(drafted.Path, kind) { Note = note }
                : new RestorePreviewFile(drafted.Path, kind) { CurrentText = now.Text ?? "", RestoredText = restored.Text ?? "" });
        }
        return files;
    }

    public async Task<RestoreOutcome> RestoreAsync(string sessionId, string checkpointId, AppScope scope, CancellationToken cancellationToken = default)
    {
        var (ledger, entry, session) = await FindAsync(sessionId, checkpointId, cancellationToken).ConfigureAwait(false);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var label = $"rollback to before {checkpointId}";
        // The kernel rolls back to just before the call (its position minus one): the call's turn keeps its prompt, and only
        // the turns after it leave whole, as the plan says.
        var plan = session.PlanRollbackTo(checkpointId) ?? throw Missing(sessionId, checkpointId);
        var firstGone = ledger.TurnAt(plan.Position) + 1;
        var partial = ledger.TurnAt(entry.CallSequence) == firstGone - 1;
        var result = await session.RollbackToAsync(checkpointId, Scope(scope), null, cancellationToken).ConfigureAwait(false);
        var at = _time.GetUtcNow();
        RestoreOutcome outcome = result is null
            ? new RestoreOutcome(label, at, scope) { Refused = true, Detail = $"{checkpointId} is no longer in the ledger. Nothing was changed." }
            : new RestoreOutcome(label, at, scope)
            {
                Restored = result.Restored,
                Merged = result.Merged,
                Conflicts = [.. result.Conflicts.Select(conflict => new AppConflict(conflict.Path, conflict.Reason))],
                CheckpointId = result.CheckpointId,
                Refused = result.Status == RestoreStatus.Refused,
                Detail = result.Status == RestoreStatus.Refused ? Sentence(result.Detail) + " Nothing was overwritten." : null,
            };
        var head = await Task.Run(() => runtime.Log.GetHead(sessionId, CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        if (outcome.CheckpointId is { } restorePoint)
            _outcomes[(sessionId, restorePoint)] = outcome;
        else
            _unrecorded[sessionId] = (outcome, head);
        // The open transcript follows as a replay of the log would show it (unless only files went back): the turns after the
        // call's leave, the call's turn keeps its prompt and what came before the call, and the files put back are marked.
        if (result is not null)
            _signals.Rewound(sessionId, new AppRewound(firstGone, scope, "rollback")
            {
                FromCallId = partial ? entry.CallId : null,
                UnansweredError = partial ? SessionUpdateTranslator.NoAnswer : null,
                Restored = outcome.Restored,
                Merged = outcome.Merged,
                Conflicts = outcome.Conflicts,
                CheckpointId = outcome.CheckpointId,
                RestorePoint = checkpointId,
                Refused = outcome.Refused,
                Detail = outcome.Detail,
            });
        _signals.Raise(sessionId);
        return outcome;
    }

    public void Dispose() => _signals.Changed -= OnChanged;

    private void OnChanged(string sessionId) => TimelineChanged?.Invoke(this, sessionId);

    /// <summary>The session's ledger, the entry before which the checkpoint is, and the session's handle to plan and restore with.</summary>
    private async Task<(SessionLedger Ledger, KernelEntry Entry, CorvidSession Session)> FindAsync(string sessionId, string checkpointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointId);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var ledger = await Task.Run(() => SessionLedger.Load(runtime, sessionId, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (ledger.Restores.Any(restore => restore.Checkpoint.CheckpointId == checkpointId))
            throw new InvalidOperationException($"Going back to before a restore point ({checkpointId}) is not built on the engine: the kernel's " +
                "rollback reverts tool calls, not restores. Select the call before it.");
        var entry = ledger.Entries.FirstOrDefault(candidate => candidate.Before.Checkpoint.CheckpointId == checkpointId)
            ?? throw Missing(sessionId, checkpointId);
        var running = runtime.TryGetSession(sessionId, out var open);
        if (entry.After is null && running && !ledger.Finished.Contains(entry.CallId))
            throw new InvalidOperationException($"The call at {checkpointId} has not finished; there is nothing of it to go back from yet.");
        var session = running ? open : runtime.OpenSession(sessionId, EngineSessionService.Surface);
        return (ledger, entry, session);
    }

    private static KeyNotFoundException Missing(string sessionId, string checkpointId) =>
        new($"No checkpoint {checkpointId} in the ledger of {sessionId}.");

    private static async Task<AppLedgerEntry> EntryAsync(KernelEntry entry, SessionLedger ledger, LedgerTexts texts, bool open)
    {
        var id = entry.Before.Checkpoint.CheckpointId;
        var at = ledger.TimeOf(entry.Before.Sequence);
        var turn = ledger.TurnAt(entry.CallSequence);
        var target = ledger.Calls.TryGetValue(entry.CallId, out var call) ? ToolPresentation.Target(call.Arguments) : null;
        if (entry.After is { } after)
            return new AppLedgerEntry(id, LedgerEntryKind.Call, at, turn)
            {
                Tool = entry.Tool,
                Target = target,
                AfterId = after.Checkpoint.CheckpointId,
                Changes = await texts.ChangesAsync(entry).ConfigureAwait(false),
            };
        // No after checkpoint: still running here (no result yet, in a session this process has open), or it ended without one.
        var pending = open && !ledger.Finished.Contains(entry.CallId);
        return new AppLedgerEntry(id, pending ? LedgerEntryKind.Pending : LedgerEntryKind.Call, at, turn)
        {
            Tool = entry.Tool,
            Target = target,
            Stopped = !pending,
        };
    }

    /// <summary>
    /// A restore point's result: as this process saw it, or as the log tells it (its checkpoint's paths were put back; a
    /// rewind right after it moved the transcript too). The log does not say which paths were merged.
    /// </summary>
    private RestoreOutcome OutcomeOf(string sessionId, RecordedCheckpoint restore, List<(long Sequence, SessionRewound Rewound)> rewinds,
        SessionLedger ledger)
    {
        if (_outcomes.TryGetValue((sessionId, restore.Checkpoint.CheckpointId), out var known))
            return known;
        var rewound = rewinds.FirstOrDefault(rewind => rewind.Rewound.CheckpointId == restore.Checkpoint.CheckpointId).Rewound;
        return new RestoreOutcome(rewound?.Reason ?? "files put back", ledger.TimeOf(restore.Sequence),
            rewound is null ? AppScope.FilesOnly : AppScope.FilesAndConversation)
        {
            Restored = restore.Checkpoint.Paths ?? [],
            CheckpointId = restore.Checkpoint.CheckpointId,
        };
    }

    /// <summary>The latest restore: the latest the log has, unless one from here that took no checkpoint came after it.</summary>
    private RestoreOutcome? LastRestore(string sessionId, SessionLedger ledger, List<(long Sequence, SessionRewound Rewound)> rewinds)
    {
        RestoreOutcome? logged = null;
        long loggedAt = 0;
        if (ledger.Restores.LastOrDefault() is { } restore)
            (logged, loggedAt) = (OutcomeOf(sessionId, restore, rewinds, ledger), restore.Sequence);
        // A conversation-only rewind takes no checkpoint: the log has only its SessionRewound.
        if (rewinds.LastOrDefault() is { Rewound: { CheckpointId: null } bare } last && last.Sequence > loggedAt)
            (logged, loggedAt) = (new RestoreOutcome(bare.Reason, ledger.TimeOf(last.Sequence), AppScope.ConversationOnly), last.Sequence);
        if (_unrecorded.TryGetValue(sessionId, out var mine) && mine.Head >= loggedAt)
            return mine.Outcome;
        return logged;
    }

    private static RestoreScope Scope(AppScope scope) => scope switch
    {
        AppScope.ConversationOnly => RestoreScope.ConversationOnly,
        AppScope.FilesOnly => RestoreScope.FilesOnly,
        _ => RestoreScope.FilesAndConversation,
    };

    private static string Sentence(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "The restore was refused." : char.ToUpperInvariant(text[0]) + text[1..].TrimEnd('.') + ".";
}
