using System.Collections.Immutable;
using Corvid.App.Core.Sessions;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;
using HostPlan = Corvid.Host.Sessions.PlanChanged;
using ApprovalAssessed = Corvid.Abstractions.Events.ApprovalAssessed;
using ApprovalResolutions = Corvid.Abstractions.Events.ApprovalResolutions;

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
/// A replayed turn names the generator its first decision saw (<see cref="PromptSent.Generator"/>) and carries its
/// decisions, taint rows and compaction markers as the live one did. What the log does not record stays unset rather than
/// guessed: a past turn's tokens, and the outcome beyond "answered" (a replayed turn without an answer was interrupted, cut
/// by a rollback or stopped with its process, and ends as interrupted, as the live turn did).
/// </para>
/// <para>
/// A steer (K-3) is a <see cref="UserMessage"/> marked <see cref="UserMessage.Steered"/> inside the turn that took it. A
/// clarifying question (K-6) stays open until the person's next message, which answers it
/// (<see cref="QuestionAnswered"/> just before that message): live or replayed alike, nothing else closes it. A step (turn
/// progress) is a <see cref="TurnProgress"/>; a running call's tail (K-2) a <see cref="ToolCallUpdate.Tail"/>.
/// </para>
/// </summary>
public sealed partial class SessionUpdateTranslator(string workspace)
{
    private readonly string _workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
    private readonly List<SessionUpdate> _early = [];
    private bool _answered;
    private DateTimeOffset _turnStarted;
    private DateTimeOffset _last;
    // The log position of each turn's prompt, in turn order, for a compaction's range in turns.
    private readonly List<long> _turnStarts = [];
    private long _turnTokens;
    // K-6: the question the latest clarifying turn ended on, until the next message answers it; and its text while its turn
    // is the latest, so the answer that repeats it is not shown twice.
    private string? _openQuestion;
    private string? _questionText;
    // A4V2-4: the turn's remember checkpoint chose to write, so the memory gate asked after its answer is the turn's; and
    // whether the turn's latest output is answer text (a call after it, such as memory_add, asks the gate in the turn).
    private bool _turnRemembers;
    private bool _answerLast;
    // A5: the generator the running step is on (the turn's start, then each ModelSwitched of the generator), for the step
    // update and tool cards; and the latest switch's log position, so a switch the replay and the live event both bring is
    // shown once.
    private string _stepModel = "";
    private long _modelSequence;

    /// <summary>The generator a turn opened from here on starts on ("fake-gen"); empty when unknown (a replay).</summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// The next turn opened is a <c>!</c> line's (A4V2-5): it runs its command and no generator, so its header names none,
    /// as its replay (whose log has no decision to name one) does. Taken by that turn's prompt.
    /// </summary>
    public bool ShellLine { get; set; }

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

    /// <summary>The open clarifying question's id (K-6): asked, and no message since. Null when none is open.</summary>
    public string? OpenQuestion => _openQuestion;

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
        // The prompts of the turns that left start no turn now.
        if (_turnStarts.Count > Turn)
            _turnStarts.RemoveRange(Turn, _turnStarts.Count - Turn);
    }

    /// <summary>
    /// What a turn the window interrupted ends with, and a replayed turn without an answer: the log records no end for an
    /// interrupted turn, one a rollback cut or one whose process stopped, and each of them reads as interrupted.
    /// </summary>
    public const string Interrupted = "interrupted";

    /// <summary>The taint so far (<see cref="TaintNoted"/>, replayed or live), for a replay to reconcile with the session's own.</summary>
    public bool Tainted { get; private set; }

    public IReadOnlyList<SessionUpdate> Translate(SessionStreamUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var output = new List<SessionUpdate>(2);
        var at = update.At;
        var outside = false;
        switch (update)
        {
            case PromptSent prompt:
                // A replayed turn ends where the next prompt starts.
                if (TurnOpen)
                    output.AddRange(Close(null, _last, null, null));
                Turn++;
                TurnOpen = true;
                _answered = false;
                _answerLast = false;
                _turnStarted = at;
                _turnTokens = 0;
                _turnStarts.Add(prompt.Sequence);
                _questionText = null;
                _turnRemembers = false;
                var generator = prompt.Generator ?? (ShellLine ? "" : Model);
                ShellLine = false;
                _stepModel = generator;
                // K-6: the person's next message is their answer to the open question; nothing else answers it, a guest's
                // message in a gateway chat included (security M2: shown as the guest's, the question stays open).
                if (_openQuestion is { } open && prompt.From is null)
                {
                    _openQuestion = null;
                    output.Add(new QuestionAnswered(open, prompt.Text) { At = at });
                }
                output.Add(new UserMessage(prompt.Text) { Position = prompt.Sequence, At = at, From = prompt.From });
                // A replayed turn names the generator its first decision saw; a live one starts on the slot's.
                output.Add(new TurnStarted(Turn, generator) { At = at, MaxSteps = MaxSteps });
                output.AddRange(_early);
                _early.Clear();
                break;
            case AnswerChunk chunk:
                _answered |= chunk.Text.Length > 0;
                _answerLast |= chunk.Text.Length > 0;
                // K-6: a clarifying turn's answer is its question, which the question's block shows: not twice.
                if (_questionText is { } asked && string.Equals(chunk.Text.Trim(), asked, StringComparison.Ordinal))
                    break;
                output.Add(new AgentMessageChunk(chunk.Text) { At = at });
                break;
            // K-3: a steer the running turn took, in that turn.
            case SteerSent steer:
                output.Add(new UserMessage(steer.Text) { Position = steer.Sequence, Steered = true, At = at });
                break;
            // A5: the person answered it inside the turn, which went on with the answer.
            case QuestionClosed closed:
                if (_openQuestion == closed.QuestionId)
                    _openQuestion = null;
                _questionText = null;
                output.Add(new QuestionAnswered(closed.QuestionId, closed.Answer) { At = at });
                break;
            // K-6: the question the turn ended on; it stays open past the turn's end, until the next message.
            case QuestionRaised question:
                _openQuestion = question.QuestionId;
                _questionText = question.Text.Trim();
                output.Add(new QuestionAsked(question.QuestionId, question.Text)
                {
                    At = at,
                    // A short title for the tray when the question is longer than one; a one-line question is its own.
                    Title = QuestionTitle(question.Text) is var title && title != question.Text.Trim() ? title : null,
                    Options = question.Options.IsDefault ? [] : question.Options,
                });
                break;
            // Turn progress: the step a response with tool calls began, with the turn's budget when known.
            case TurnStepped stepped when TurnOpen:
                // A5: the step's generator, from its calls' log on replay; live, the latest one known (its calls then say).
                if (stepped.Generator is { } stepGenerator)
                    _stepModel = Bare(stepGenerator);
                output.Add(new TurnProgress(stepped.Step) { At = at, MaxSteps = stepped.MaxSteps ?? MaxSteps, Model = StepModel });
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
                    // A5: a replayed record that sums several calls names the latest one's prompt.
                    ContextTokens = (usage.PromptTokens ?? usage.InputTokens) is { } sent ? (int)Math.Min(int.MaxValue, sent) : null,
                    ContextWindow = ContextWindow,
                });
                break;
            // K-6: ask_user's call is the question's: its block shows it, so no tool card does.
            case ToolCallStarted { Tool: Corvid.Kernel.Turns.AskUserTool.ToolName }:
            case ToolCallChanged { Tool: Corvid.Kernel.Turns.AskUserTool.ToolName }:
                break;
            case ToolCallStarted started:
                // A4: a ! line's turn is its shell call (TurnRunner's user-shell ids); it has no answer, and needs none.
                var userShell = started.CallId.StartsWith(Corvid.Kernel.Turns.TurnRunner.UserShellCall, StringComparison.Ordinal);
                _answered |= userShell;
                _answerLast = userShell;
                output.Add(new ToolCall(started.CallId, started.Tool ?? started.Title, Kind(started.Kind, started.Tool), Status(started.Status))
                {
                    At = at,
                    Model = userShell ? null : CallModel(started.Generator),
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
                        Model = changed.CallId.StartsWith(Corvid.Kernel.Turns.TurnRunner.UserShellCall, StringComparison.Ordinal) ? null : CallModel(changed.Generator),
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
                    Verdict = Verdict(changed),
                    // K-2: the running call's last lines, replacing the previous tail.
                    Tail = changed.Tail,
                });
                break;
            case HostPlan plan:
                output.Add(new PlanUpdate([.. plan.Items.Select(item => new PlanEntry(item.Text, item.Completed ? PlanEntryStatus.Completed : PlanEntryStatus.Pending))])
                {
                    At = at,
                });
                break;
            // A4V2-4: the memory gate asked for a # line, which logs no message: it belongs to no turn. Live it shows only as
            // the line's notice, so a replay neither lists it in the turn before nor runs that turn's time on to it.
            case DecisionsMade made when OutsideTurn(made.Decisions):
                outside = true;
                break;
            case DecisionsMade made:
                _turnRemembers |= TurnOpen && made.Decisions.Questions.Any(question => question is { Question: Remember, Action: StartMemoryWrite, Acted: true });
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
            case TaintNoted taint:
                Tainted = taint.Tainted;
                output.Add(new TaintChanged(taint.Tainted) { At = at, Source = taint.Source });
                break;
            case CompactionLogged logged:
                var summary = logged.Compaction.Summary;
                output.Add(new CompactionUpdate(Math.Max(1, TurnAt(logged.Compaction.FromSequence)), Math.Max(1, TurnAt(logged.Compaction.ThroughSequence)))
                {
                    At = at,
                    Goal = summary.Goal,
                    Constraints = summary.Constraints.IsDefault ? [] : summary.Constraints,
                    Decisions = summary.Decisions.IsDefault ? [] : summary.Decisions,
                    Files = summary.Files.IsDefault ? [] : summary.Files,
                    OpenQuestions = summary.OpenQuestions.IsDefault ? [] : summary.OpenQuestions,
                    NextStep = summary.NextStep,
                    Summarizer = logged.Compaction.Summarizer,
                    Focus = logged.Compaction.Focus,
                    Expanded = logged.Expanded,
                });
                break;
            // A5: a slot's model changed (live from this process, replayed from the log's ModelSwitched): its row and slot change.
            case ModelChanged change:
                output.AddRange(Models(change, at));
                break;
            // A5: a child session started or ended (replayed with ReplayOptions.Children, live from runtime.ChildChanged).
            case ChildStarted or ChildEnded:
                outside = true;
                output.AddRange(Child(update, replayed: true));
                break;
            // CheckpointLogged: Rewind's timeline reads the ledger itself (A3 history); the transcript shows no row for it.
        }
        // A compaction comes between turns, and so does a # line's memory gate: their time is not the replayed turn's end.
        if (at > _last && update is not CompactionLogged && !outside)
            _last = at;
        return output;
    }

    private const string MemoryOp = "memory_op";
    private const string Remember = "remember";
    private const string StartMemoryWrite = "start_memory_write";

    /// <summary>
    /// A run of decisions that only asks the memory gate (<c>memory_op</c>), outside a turn or after its answer (answer text
    /// with no call since) when the turn's own remember checkpoint did not write: a <c>#</c> line's (A4V2-4). One a turn's
    /// <c>memory_add</c> call asks follows that call, and one its remember checkpoint asks follows a remember that acted.
    /// A lesson is gated later, in the background, outside the turn live as well.
    /// </summary>
    private bool OutsideTurn(CheckpointDecisions decisions) =>
        !decisions.Questions.IsDefaultOrEmpty && decisions.Questions.All(question => question.Question == MemoryOp)
        && (!TurnOpen || (_answerLast && !_turnRemembers));

    /// <summary>The question in a few words for the attention tray: its first line, cut to a short title.</summary>
    /// <summary>The generator the running step is on, without its device; null when unknown.</summary>
    private string? StepModel => _stepModel.Length > 0 ? _stepModel : null;

    /// <summary>A5: the generator a call names (the log's, exact) or else the step's; the step follows the call's.</summary>
    private string? CallModel(string? generator)
    {
        if (generator is { Length: > 0 })
            _stepModel = Bare(generator);
        return StepModel;
    }

    /// <summary>
    /// A5: the host's <see cref="ModelChanged"/> as the app's updates: a <see cref="SlotModelChanged"/> for any slot, and a
    /// transcript row (<see cref="ModelSwitched"/>) before it when a logged switch really changed the model: the generator's
    /// for every reason (escalation, a fallback, the privacy gate's retreat, de-escalation, the person's switch), the other
    /// slots' (the decider's) for the person's own. Names drop the device (<c>qwen3.5-0.8b@cuda0</c> reads
    /// <c>qwen3.5-0.8b</c>), as the session's model does. A person's switch while a turn runs says the turn takes it at its
    /// next step. A switch already shown (by its log position) is not shown again.
    /// </summary>
    internal IReadOnlyList<SessionUpdate> Models(ModelChanged change, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Sequence is { } sequence)
        {
            if (sequence <= _modelSequence)
                return [];
            _modelSequence = sequence;
        }
        var slot = new SlotModelChanged(change.Slot, change.From, change.To, change.Reason)
        {
            At = at,
            SessionDefault = change.SessionDefault,
            Pinned = change.Pinned,
            Cloud = change.Cloud,
        };
        if (change.Sequence is null || change.From == change.To)
            return [slot];
        var generator = change.Slot == Corvid.Models.ModelSlots.Generator;
        var person = change.Reason is "session" or "user";
        // Between turns the next turn starts on it; within one, the calls the log ties to it say when it took over.
        if (generator && !TurnOpen)
            _stepModel = Bare(change.To);
        if (!generator && !person)
            return [slot];
        var next = TurnOpen && person ? "; the running turn takes it at its next step" : "";
        var (kind, reason) = change.Reason switch
        {
            "escalation" => (ModelSwitchKind.Escalated, "escalated"),
            "fallback" => (ModelSwitchKind.FellBack, "the model failed"),
            "privacy" => (ModelSwitchKind.KeptLocal, "the privacy gate kept it local"),
            // A de-escalation is the turn's end; one before the turn answered (or a forgotten choice, live) is a reset.
            "de_escalation" when change.SessionDefault || (TurnOpen && !_answered) => (ModelSwitchKind.Returned, "this session's choice was reset to the default"),
            "de_escalation" => (ModelSwitchKind.Returned, "the turn ended"),
            "user" => (ModelSwitchKind.Switched, "for this turn" + next),
            _ => (ModelSwitchKind.Switched, "for this session" + next),
        };
        return
        [
            new ModelSwitched(change.Slot, Bare(change.From), Bare(change.To), kind, reason) { At = at, Cloud = change.Cloud },
            slot,
        ];
    }

    private static string Bare(string profileId) => profileId.Split('@')[0];

    internal static string QuestionTitle(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return line.Length > 60 ? line[..59].TrimEnd() + "…" : line;
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
        // A replayed turn the log holds no answer for did not finish: interrupted, as the live one ended.
        var known = outcome ?? (_answered ? TurnOutcome.Answered : TurnOutcome.Cancelled);
        return
        [
            new TurnEnded(Turn, known)
            {
                At = at,
                Elapsed = elapsed ?? (at > _turnStarted ? at - _turnStarted : TimeSpan.Zero),
                Error = error ?? (outcome is null && !_answered ? Interrupted : null),
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

    /// <summary>
    /// A finished call's verdict: an asked call's answer (replay; live, the card's prompter closed it), else why it ran or
    /// was refused unasked (G-2: the pipeline's assessment), else "read-only" for a tool that declared itself so; null when
    /// none of these is known, which leaves the card's verdict as it was.
    /// </summary>
    internal static CallVerdict? Verdict(ToolCallChanged changed) => changed switch
    {
        { Approval: { } approval } => Verdict(approval) with { CheckpointId = approval.Approved ? changed.CheckpointBefore : null },
        { Assessment: { } assessed } => Verdict(assessed, changed.CheckpointBefore),
        { ReadOnly: true } => new CallVerdict(VerdictKind.ReadOnly),
        _ => null,
    };

    /// <summary>
    /// The pipeline's assessment of a call that asked nobody, in the card's terms: the rule that allowed or refused it (with
    /// its scope), the mode and the <c>risk</c> level that let it run ("in workspace, no network"), the hardline rule or
    /// the missing approver that refused it, and the checkpoint taken before it.
    /// </summary>
    internal static CallVerdict Verdict(ApprovalAssessed assessed, string? checkpoint)
    {
        var kind = assessed.Resolution switch
        {
            ApprovalResolutions.RuleAllowed => assessed.RuleScope == "always" ? VerdictKind.AlwaysRule : VerdictKind.SessionRule,
            ApprovalResolutions.RuleDenied => VerdictKind.RuleDenied,
            ApprovalResolutions.Hardline => VerdictKind.Hardline,
            ApprovalResolutions.NoApprover => VerdictKind.NoApprover,
            _ when assessed.Reasons.Any(reason => reason.Code == "read_only") => VerdictKind.ReadOnly,
            _ => VerdictKind.Mode,
        };
        var allowed = kind is VerdictKind.Mode or VerdictKind.SessionRule or VerdictKind.AlwaysRule or VerdictKind.ReadOnly;
        return new CallVerdict(kind)
        {
            Rule = assessed.Rule,
            Mode = kind == VerdictKind.Mode ? Mode(assessed.Mode) : null,
            RiskLevel = allowed ? assessed.Risk?.Level : null,
            RiskConfidence = allowed ? assessed.Risk?.Confidence : null,
            CheckpointId = allowed ? checkpoint : null,
            Detail = kind switch
            {
                VerdictKind.Mode => Scope(assessed),
                // The reasons that refused it; for a remembered denial, the rule says which.
                VerdictKind.Hardline or VerdictKind.NoApprover => string.Join("; ", assessed.Reasons.Select(reason => reason.Detail)),
                VerdictKind.RuleDenied => assessed.Rule ?? string.Join("; ", assessed.Reasons.Select(reason => reason.Detail)),
                _ => null,
            },
        };
    }

    /// <summary>What a call that ran unasked does, as the card says it: "in workspace, no network".</summary>
    private static string? Scope(ApprovalAssessed assessed)
    {
        var parts = new List<string>();
        if (assessed.Contained is { } contained)
            parts.Add(contained ? "in workspace" : "reaches outside the workspace");
        if (assessed.Effects is { } effects)
            parts.Add(effects.Contains("network") ? "network" : "no network");
        if (assessed.Risk is { Calibrated: false, Decider: var decider })
            parts.Add($"the risk answer was not calibrated ({decider ?? "degraded tier"})");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static Corvid.App.Core.Shell.ApprovalMode? Mode(string mode) => mode switch
    {
        "manual" => Corvid.App.Core.Shell.ApprovalMode.Manual,
        "smart" => Corvid.App.Core.Shell.ApprovalMode.Smart,
        "permissive" => Corvid.App.Core.Shell.ApprovalMode.Permissive,
        _ => null,
    };

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
