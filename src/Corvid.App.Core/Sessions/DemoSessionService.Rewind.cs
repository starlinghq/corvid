using System.Globalization;

namespace Corvid.App.Core.Sessions;

public sealed partial class DemoSessionService
{
    /// <summary>The restore points the demo's restores take start above the checkpoints the scripted turns use (c-45 to c-48).</summary>
    private const int FirstRestoreNumber = 49;

    /// <summary>
    /// The id the session's next restore point takes: above every checkpoint in its ledger and the scripted turns' ones.
    /// Rewind's restores and Review's per-file reverts both take theirs here, so the two never collide.
    /// </summary>
    internal string NextRestorePoint(string sessionId)
    {
        var ledger = Writable(sessionId).State.Ledger;
        var highest = ledger.Select(row => Number(row.Id)).Concat(ledger.Select(row => Number(row.AfterId))).DefaultIfEmpty(0).Max();
        return string.Create(CultureInfo.InvariantCulture, $"c-{Math.Max(FirstRestoreNumber, highest + 1)}");
    }

    /// <summary>
    /// The demo's restore, as Rewind's and Review's demo services ask for it: a running turn stops first (a rewind never
    /// runs beside it; a per-file revert, <paramref name="stopRunningTurn"/> false, leaves it running), then the restore
    /// checkpoint and the rewind go on the session's stream, so the fold drops the turns that left, marks the files put
    /// back in the ledger and the change lists, and the transcript shows the marker. A refused restore records no
    /// checkpoint and changes nothing but the marker. The engine's rollback emits the same two updates from
    /// <c>CheckpointTaken</c> and <c>SessionRewound</c>.
    /// </summary>
    internal void Rewind(string sessionId, CheckpointRecorded? restorePoint, SessionRewound rewound, bool stopRunningTurn = true)
    {
        ArgumentNullException.ThrowIfNull(rewound);
        var session = Writable(sessionId);
        if (stopRunningTurn && !rewound.Refused && session.State.Running)
            StopForRewind(session);
        if (restorePoint is not null && !rewound.Refused)
            Emit(session, restorePoint);
        Emit(session, rewound);
    }

    private static int Number(string? checkpointId) =>
        checkpointId is not null
        && int.TryParse(checkpointId.AsSpan(checkpointId.LastIndexOf('-') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    /// <summary>Ends the running turn as cancelled, leaving the queue for the person to send or take back.</summary>
    private void StopForRewind(DemoSession session)
    {
        session.Script?.End();
        session.Script = null;
        session.Resume = null;
        session.QuestionHandler = null;
        var state = session.State;
        foreach (var open in state.Approvals)
            Emit(session, new ApprovalResolved(open.ApprovalId, ApprovalOutcome.Cancelled) { Reason = "rewound" });
        foreach (var running in state.Transcript.OfType<ToolEntry>().Where(tool => tool.Turn == state.Turn && tool.Status == ToolCallStatus.InProgress))
        {
            Emit(session, new ToolCallUpdate(running.CallId)
            {
                Status = ToolCallStatus.Failed,
                Outcome = ToolOutcome.Cancelled,
                Elapsed = _clock - running.StartedAt,
            });
        }

        Emit(session, new TurnEnded(state.Turn, TurnOutcome.Cancelled)
        {
            Elapsed = state.TurnStartedAt is { } started ? _clock - started : null,
        });
    }
}
