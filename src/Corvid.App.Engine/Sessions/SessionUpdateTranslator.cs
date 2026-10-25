using System.Collections.Immutable;
using Corvid.App.Core.Sessions;
using Corvid.Host.Sessions;
using HostPlan = Corvid.Host.Sessions.PlanChanged;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// The host's session update stream (K-10, <see cref="SessionStreamUpdate"/>) as the app's update contract
/// (<see cref="SessionUpdate"/>, Corvid.App.Core/Sessions/*Updates.cs): one translator per session, fed the replayed
/// history first and the live turns after it, so the turn numbers and the open turn carry over and a loaded session folds
/// to what the live one showed. Not thread-safe: the caller feeds it one update at a time (the session's lock).
/// <para>
/// Turns: the stream has no turn marks, so a user's message opens one (<see cref="UserMessage"/> then
/// <see cref="TurnStarted"/>, the order <see cref="SessionFold"/> expects); a live turn is closed by
/// <see cref="EndTurn"/> with its delivery's outcome, a replayed one by the next message or <see cref="EndReplay"/>.
/// What the log does not record stays unset rather than guessed: the generator a past turn ran on, its tokens, the
/// outcome beyond "answered" (a replayed turn without an answer is marked failed, saying the log has none).
/// </para>
/// </summary>
public sealed class SessionUpdateTranslator(string workspace)
{
    private readonly string _workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
    private readonly List<SessionUpdate> _early = [];
    private bool _answered;
    private DateTimeOffset _turnStarted;
    private DateTimeOffset _last;
    // The log position of each turn's prompt, in turn order, for a compaction's range in turns.
    private readonly List<long> _turnStarts = [];
    private long _turnTokens;

    /// <summary>The generator a turn opened from here on starts on ("fake-gen"); empty when unknown (a replay).</summary>
    public string Model { get; set; } = "";

    /// <summary>The step budget a turn opened from here on has (<c>Corvid:Turn:MaxSteps</c>); null when unknown (a replay).</summary>
    public int? MaxSteps { get; set; }

    /// <summary>The generator's accepted context window now, for the gauge a live call's usage fills; null when unknown.</summary>
    public int? ContextWindow { get; set; }

    /// <summary>The turn a log position falls in: the latest turn whose prompt is at or before it (0 before the first).</summary>
    public int TurnAt(long sequence)
    {
        var turn = 0;
        while (turn < _turnStarts.Count && _turnStarts[turn] <= sequence)
            turn++;
        return turn;
    }

    /// <summary>The latest turn's number (0 before the first).</summary>
    public int Turn { get; private set; }

    /// <summary>A turn has started and not ended.</summary>
    public bool TurnOpen { get; private set; }

    /// <summary>
    /// The conversation went back to before <paramref name="firstTurnGone"/> (a rollback from Rewind): the latest turn is
    /// the one before it, so the next prompt is numbered as a replay of the log would number it.
    /// </summary>
    public void RewindTo(int firstTurnGone)
    {
        Turn = Math.Min(Turn, Math.Max(0, firstTurnGone - 1));
        TurnOpen = false;
    }

    public IReadOnlyList<SessionUpdate> Translate(SessionStreamUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var output = new List<SessionUpdate>(2);
        var at = update.At;
        switch (update)
        {
            case PromptSent prompt:
                // A replayed turn ends where the next prompt starts.
                if (TurnOpen)
                    output.AddRange(Close(null, _last, null, null));
                Turn++;
                TurnOpen = true;
                _answered = false;
                _turnStarted = at;
                _turnTokens = 0;
                _turnStarts.Add(prompt.Sequence);
                output.Add(new UserMessage(prompt.Text) { Position = prompt.Sequence, At = at });
                output.Add(new TurnStarted(Turn, Model) { At = at, MaxSteps = MaxSteps });
                output.AddRange(_early);
                _early.Clear();
                break;
            case AnswerChunk chunk:
                _answered |= chunk.Text.Length > 0;
                output.Add(new AgentMessageChunk(chunk.Text) { At = at });
                break;
            case ThoughtChunk thought:
                output.Add(new AgentThoughtChunk(thought.Text) { At = at });
                break;
            case UsageReported usage:
                // K-5 as far as a call reports it: the turn's tokens so far, and the prompt the latest call sent against the
                // generator's window. Cost and the prompt's breakdown are not reported.
                _turnTokens += (usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0);
                output.Add(new UsageUpdate
                {
                    At = at,
                    TurnTokens = (int)Math.Min(int.MaxValue, _turnTokens),
                    ContextTokens = usage.InputTokens is { } sent ? (int)Math.Min(int.MaxValue, sent) : null,
                    ContextWindow = ContextWindow,
                });
                break;
            case ToolCallStarted started:
                output.Add(new ToolCall(started.CallId, started.Tool ?? started.Title, Kind(started.Kind, started.Tool), Status(started.Status))
                {
                    At = at,
                    Target = ToolPresentation.Target(started.Input),
                    Input = started.Input?.GetRawText(),
                    Locations = Relative(started.Locations),
                });
                break;
            case ToolCallChanged changed:
                // A live call is announced before its input is read; the finished call brings it, as an announcement would.
                if (changed.Input is { } input && changed.Tool is { } tool)
                    output.Add(new ToolCall(changed.CallId, tool, Kind(changed.Kind ?? ToolCallKinds.Other, tool),
                        Status(changed.Status ?? ToolCallState.InProgress))
                    {
                        At = at,
                        Target = ToolPresentation.Target(input),
                        Input = input.GetRawText(),
                        Locations = Relative(changed.Locations),
                    });
                output.Add(new ToolCallUpdate(changed.CallId)
                {
                    At = at,
                    Status = changed.Status is { } status ? Status(status) : null,
                    Outcome = Outcome(changed.Outcome, changed.Status),
                    Target = changed.Input is { } args ? ToolPresentation.Target(args) : null,
                    Output = changed.OutputText,
                    Elapsed = changed.Elapsed,
                    Diffs = [.. changed.Diffs.Select(diff => new FileEdit(RelativePath(diff.Path), diff.OldText, diff.NewText))],
                    Locations = Relative(changed.Locations),
                    Verdict = changed.Approval is { } approval ? Verdict(approval) : null,
                });
                break;
            case HostPlan plan:
                output.Add(new PlanUpdate([.. plan.Items.Select(item => new PlanEntry(item.Text, item.Completed ? PlanEntryStatus.Completed : PlanEntryStatus.Pending))])
                {
                    At = at,
                });
                break;
            case DecisionsMade made:
                var decided = new CheckpointDecided(made.Decisions.Checkpoint, made.Decisions.Decider,
                    [.. made.Decisions.Questions.Select(question => new DecisionAnswer(question.Question, question.Confidence, question.Action, question.Acted))])
                {
                    At = at,
                };
                // A checkpoint that decides before the turn's prompt is in the log (ingress) belongs to the turn it opens.
                if (TurnOpen)
                    output.Add(decided);
                else
                    _early.Add(decided);
                break;
            // CheckpointLogged: Rewind's timeline reads the ledger itself (A3 history); the transcript shows no row for it.
        }
        if (at > _last)
            _last = at;
        return output;
    }

    /// <summary>Ends the live turn with its delivery's outcome; nothing when no turn is open (its message never arrived).</summary>
    public IReadOnlyList<SessionUpdate> EndTurn(TurnOutcome outcome, DateTimeOffset at, TimeSpan? elapsed = null, string? error = null)
    {
        if (TurnOpen)
            return Close(outcome, at, elapsed, error);
        // Decisions held for a prompt that never came belong to no turn.
        _early.Clear();
        return [];
    }

    /// <summary>Ends the last replayed turn, unless it is still running in this process (<paramref name="running"/>).</summary>
    public IReadOnlyList<SessionUpdate> EndReplay(bool running = false) => TurnOpen && !running ? Close(null, _last, null, null) : [];

    private List<SessionUpdate> Close(TurnOutcome? outcome, DateTimeOffset at, TimeSpan? elapsed, string? error)
    {
        TurnOpen = false;
        var known = outcome ?? (_answered ? TurnOutcome.Answered : TurnOutcome.Failed);
        return
        [
            new TurnEnded(Turn, known)
            {
                At = at,
                Elapsed = elapsed ?? (at > _turnStarted ? at - _turnStarted : TimeSpan.Zero),
                Error = error ?? (outcome is null && !_answered ? "The log holds no answer for this turn." : null),
                // Live only (K-5): a replay knows no usage.
                Tokens = _turnTokens > 0 ? (int)Math.Min(int.MaxValue, _turnTokens) : null,
            },
        ];
    }

    /// <summary>The card's kind: ACP's, with the design's memory and MCP kinds told by the tool's name.</summary>
    internal static ToolKind Kind(string kind, string? tool) => tool switch
    {
        { } name when name.StartsWith("memory_", StringComparison.Ordinal) => ToolKind.Memory,
        { } name when name.Contains("__", StringComparison.Ordinal) => ToolKind.Mcp,
        _ => kind switch
        {
            ToolCallKinds.Read => ToolKind.Read,
            ToolCallKinds.Edit => ToolKind.Edit,
            ToolCallKinds.Delete => ToolKind.Delete,
            ToolCallKinds.Search => ToolKind.Search,
            ToolCallKinds.Execute => ToolKind.Execute,
            ToolCallKinds.Fetch => ToolKind.Fetch,
            _ => ToolKind.Other,
        },
    };

    private static ToolCallStatus Status(ToolCallState state) => state switch
    {
        ToolCallState.Pending => ToolCallStatus.Pending,
        ToolCallState.InProgress => ToolCallStatus.InProgress,
        ToolCallState.Completed => ToolCallStatus.Completed,
        _ => ToolCallStatus.Failed,
    };

    /// <summary>
    /// A replayed call's approval answer as the live card closed it (<c>CardPrompter</c>): approved once, for the session or
    /// always; denied, with the person's reason; or unanswered by the deadline or cancelled.
    /// </summary>
    internal static CallVerdict Verdict(CallApproval approval)
    {
        var kind = approval switch
        {
            { Approved: true, Scope: "session" } => VerdictKind.ApprovedSession,
            { Approved: true, Scope: "always" } => VerdictKind.ApprovedAlways,
            { Approved: true } => VerdictKind.ApprovedOnce,
            { Answered: true } => VerdictKind.Denied,
            _ when approval.Reason.StartsWith("timeout", StringComparison.Ordinal) => VerdictKind.TimedOut,
            _ => VerdictKind.Cancelled,
        };
        return new CallVerdict(kind)
        {
            ApprovalId = approval.ApprovalId,
            Detail = kind == VerdictKind.Denied ? approval.Note : null,
        };
    }

    /// <summary>The span's outcome; a replayed call has only its status, which says succeeded or failed.</summary>
    private static ToolOutcome? Outcome(string? outcome, ToolCallState? status) => outcome switch
    {
        "succeeded" => ToolOutcome.Succeeded,
        "failed" => ToolOutcome.Failed,
        "invalid_arguments" => ToolOutcome.InvalidArguments,
        "denied" => ToolOutcome.Denied,
        "cancelled" => ToolOutcome.Cancelled,
        null when status == ToolCallState.Completed => ToolOutcome.Succeeded,
        null when status == ToolCallState.Failed => ToolOutcome.Failed,
        _ => null,
    };

    private ImmutableArray<string> Relative(ImmutableArray<string> paths) =>
        paths.IsDefaultOrEmpty ? [] : [.. paths.Select(RelativePath)];

    /// <summary>A path as the workspace names it ("src/a.cs"); one outside the workspace stays absolute.</summary>
    internal string RelativePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(_workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return full;
        return Path.GetRelativePath(_workspace, full).Replace(Path.DirectorySeparatorChar, '/');
    }
}
