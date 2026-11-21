using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// C-043: the Board's kanban columns (docs/plan/ui.md §4.13), left to right. A card's column is
/// <see cref="SessionBoard.Column"/> of its summary; the person moves a card only to Done or Parked, and anything new in the
/// session brings it back out.
/// </summary>
public enum BoardColumn
{
    /// <summary>A turn runs here or elsewhere, or a job's run has not ended.</summary>
    Running,
    /// <summary>An approval, a clarifying question, or an answer that asks the person for something ("your turn").</summary>
    NeedsYou,
    /// <summary>Idle with nothing asked, or a your-turn put aside with "Not now".</summary>
    Parked,
    /// <summary>Marked done and nothing new since, or a job's run that ended.</summary>
    Done,
}

/// <summary>
/// C-043: where a session stands on the Board, as pure functions of its summary, so the rail (<see cref="SessionHub"/>), the
/// Board's cards and its badge read it the same way. The Board's words for it are <c>BoardText</c>'s.
/// </summary>
public static class SessionBoard
{
    /// <summary>
    /// The kanban column a session's card is in, top rule first: a turn running here or elsewhere (or a job's unfinished
    /// run) is Running unless an approval waits on the person; an approval is Needs you; a Done mark is Done (and a job's
    /// ended run); a clarifying question or an answer that asks is Needs you unless the person parked it; everything else is
    /// Parked. Children are not cards, so their column is never asked.
    /// </summary>
    public static BoardColumn Column(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (summary.Job is not null)
            return summary.JobRunning ? BoardColumn.Running : BoardColumn.Done;
        if (summary.PendingApprovals == 0 && (summary.State == SessionState.Running || summary.WorkingElsewhere))
            return BoardColumn.Running;
        if (summary.PendingApprovals > 0)
            return BoardColumn.NeedsYou;
        if (summary.Mark == BoardMark.Done)
            return BoardColumn.Done;
        if (summary.PendingQuestions > 0 || summary.Ending == TurnEnding.AsksYou && summary.Mark != BoardMark.Parked)
            return BoardColumn.NeedsYou;
        return BoardColumn.Parked;
    }

    /// <summary>
    /// An idle session whose latest answer asks the person for something and that they have not parked or marked done:
    /// "your turn" on the card and the rail. A formal question or approval is "waiting on you" instead.
    /// </summary>
    public static bool YourTurn(SessionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary is { State: SessionState.Idle, Ending: TurnEnding.AsksYou, Mark: null, Job: null, PendingApprovals: 0, PendingQuestions: 0, WorkingElsewhere: false };
    }

    /// <summary>
    /// An update a person would call something that happened in the session, so it ends a Board mark: the live mirror of
    /// the host's <c>SessionCatalogue.IsNews</c> over the log, kept by the engine and the demo alike. A message (a steer
    /// too), an answer, a call and its result, an approval and its answer, a question and its answer, a plan, a rewind. A
    /// rename, a recap or a status line is not.
    /// </summary>
    public static bool IsNews(SessionUpdate update) =>
        update is UserMessage or AgentMessageChunk or TurnStarted or TurnPlanned or ToolCall or ApprovalRequested or ApprovalResolved
            or QuestionAsked or QuestionAnswered or SessionRewound
        // A call's result is the update that ends it; a running call's progress (its tail) is not news.
        || update is ToolCallUpdate { Status: ToolCallStatus.Completed or ToolCallStatus.Failed } or ToolCallUpdate { Outcome: not null };
}
