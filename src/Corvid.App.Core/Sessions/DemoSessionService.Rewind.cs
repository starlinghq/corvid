namespace Corvid.App.Core.Sessions;

public sealed partial class DemoSessionService
{
    /// <summary>
    /// The demo's restore, as Rewind's demo service asks for it: a running turn stops first (a rewind never runs beside
    /// it), then the restore checkpoint and the rewind go on the session's stream, so the fold drops the turns that left
    /// and the transcript shows the marker. A refused restore records no checkpoint and changes nothing but the marker.
    /// The engine's rollback emits the same two updates from <c>CheckpointTaken</c> and <c>SessionRewound</c>.
    /// </summary>
    internal void Rewind(string sessionId, CheckpointRecorded? restorePoint, SessionRewound rewound)
    {
        ArgumentNullException.ThrowIfNull(rewound);
        var session = Writable(sessionId);
        if (!rewound.Refused && session.State.Running)
            StopForRewind(session);
        if (restorePoint is not null && !rewound.Refused)
            Emit(session, restorePoint);
        Emit(session, rewound);
    }

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
