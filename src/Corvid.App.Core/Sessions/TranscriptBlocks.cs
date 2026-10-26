using System.Globalization;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>Which template draws a transcript block.</summary>
public enum TranscriptBlockKind
{
    /// <summary><see cref="UserMessageBlock"/>: the user's message (Main.png <c>.msg.user</c>).</summary>
    User,
    /// <summary><see cref="TurnHeaderBlock"/>: "corvid · turn 12" and its measures (<c>.turn-head</c>).</summary>
    TurnHeader,
    /// <summary><see cref="DecisionsBlock"/>: a checkpoint's chips (<c>.chips</c>).</summary>
    Decisions,
    /// <summary><see cref="SwitchBlock"/>: an escalation row (<c>.row.esc</c>).</summary>
    Switch,
    /// <summary><see cref="ToolCardBlock"/>: a tool call's card (<c>.card</c>).</summary>
    Tool,
    /// <summary><see cref="ApprovalCardBlock"/>: the open approval card (<c>section.approval</c>).</summary>
    Approval,
    /// <summary><see cref="TaintBlock"/>: taint started or cleared (<c>.row.taint</c>).</summary>
    Taint,
    /// <summary><see cref="AnswerBlock"/>: answer markdown (<c>.answer</c>).</summary>
    Answer,
    /// <summary><see cref="ThoughtBlock"/>: "thought for 4 s" (<c>.row.thought</c>).</summary>
    Thought,
    /// <summary><see cref="NoticeBlock"/>: staged memory, a held MCP tool, a finished job (<c>.row.memory</c> and others).</summary>
    Notice,
    /// <summary><see cref="DetectionBlock"/>: a detection rule's match (<c>.row.detect</c>).</summary>
    Detection,
    /// <summary><see cref="CompactionBlock"/>: a compaction marker.</summary>
    Compaction,
    /// <summary><see cref="RewindBlock"/>: a rewind marker.</summary>
    Rewind,
    /// <summary><see cref="QuestionBlock"/>: a clarifying question.</summary>
    Question,
    /// <summary><see cref="CommandBlock"/>: a slash command's output.</summary>
    Command,
}

/// <summary>What a block needs from its session to word itself and to act: the clock, the zone, and the session's actions.</summary>
internal sealed class BlockContext(SessionViewModel session, Func<DateTimeOffset> now, TimeZoneInfo zone)
{
    public SessionViewModel Session { get; } = session;

    public DateTimeOffset Now => now();

    public TimeZoneInfo Zone { get; } = zone;
}

/// <summary>
/// One block of the transcript as the page draws it: a flat list in reading order (<see cref="SessionViewModel.Blocks"/>),
/// one template per <see cref="Kind"/>. A block keeps its identity while its entry changes, so a running card turns into a
/// finished one in place, an answer grows where it is, and what a person expanded stays expanded.
/// </summary>
public abstract class TranscriptBlock : ObservableObject
{
    private int _turn;

    private protected TranscriptBlock(TranscriptEntry entry)
    {
        Id = entry.Id;
        Entry = entry;
        _turn = entry.Turn;
    }

    /// <summary>Stable while the block lives: the fold's entry id.</summary>
    public string Id { get; }

    public abstract TranscriptBlockKind Kind { get; }

    /// <summary>The turn the block belongs to; 0 outside any turn.</summary>
    public int Turn
    {
        get => _turn;
        private set => SetProperty(ref _turn, value);
    }

    /// <summary>The first block of a turn (its header, or the prompt that starts it), which the page spaces from the last.</summary>
    public bool StartsTurn => Kind == TranscriptBlockKind.User || Kind == TranscriptBlockKind.TurnHeader && Turn > 0;

    internal TranscriptEntry Entry { get; private set; }

    internal void Update(TranscriptEntry entry, BlockContext context)
    {
        if (ReferenceEquals(entry, Entry))
            return;
        Entry = entry;
        Turn = entry.Turn;
        Apply(entry, context);
    }

    /// <summary>Sets the block's properties from its entry; called once when created and again whenever the entry changes.</summary>
    internal abstract void Apply(TranscriptEntry entry, BlockContext context);

    /// <summary>Refreshes what depends on the clock (a running call's time, a countdown). Most blocks have nothing.</summary>
    internal virtual void Tick(BlockContext context)
    {
    }
}

/// <summary>A message the user sent: "you · 14:01" and the text.</summary>
public sealed class UserMessageBlock : TranscriptBlock
{
    private string _who = "";
    private string _text = "";
    private bool _isSteer;

    internal UserMessageBlock(UserEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.User;

    /// <summary>"you · 14:01", or "you · steer · 14:03" for a message delivered into a running turn.</summary>
    public string Who { get => _who; private set => SetProperty(ref _who, value); }

    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    public bool IsSteer { get => _isSteer; private set => SetProperty(ref _isSteer, value); }

    /// <summary>The message's log position, the fork point for "edit and resend" and "branch from here".</summary>
    public long? Position { get; private set; }

    /// <summary>K-8 (A6): the message's images, as bounded thumbnails with captions under its text.</summary>
    public IReadOnlyList<ImageView> Images { get; private set; } = [];

    public bool HasImages => Images.Count > 0;

    public string AutomationName => Images.Count == 0 ? $"{Who}: {Text}"
        : $"{Who}: {Text} ({SessionText.Plural(Images.Count, "image")}: {string.Join(", ", Images.Select(image => image.Caption))})";

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var user = (UserEntry)entry;
        var clock = SessionText.Clock(user.At, context.Zone);
        // Security M2: a gateway guest's message is named as theirs, never "you".
        Who = user.From is { } from ? $"{SafeText.Clean(from)} · outside content · {clock}"
            : user.Steered ? $"you · steer · {clock}" : $"you · {clock}";
        Text = SafeText.Clean(user.Text);
        IsSteer = user.Steered;
        Position = user.Position;
        if (Images.Count != user.Images.Length)
        {
            Images = [.. user.Images.Select(image => new ImageView(image, token => context.Session.ReadImageAsync(image.Digest, token)))];
            OnPropertyChanged(nameof(Images));
            OnPropertyChanged(nameof(HasImages));
        }
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>
/// A turn's header: "corvid · turn 12" and its measures ("qwen3.5-0.8b → claude · 1.2k tokens · 48 s · $0.04"). While the
/// turn runs and no usage is reported yet, the step stands in for the tokens ("claude · step 2 of 6 · 9 s").
/// </summary>
public sealed class TurnHeaderBlock : TranscriptBlock
{
    private string _who = "";
    private string _detail = "";
    private bool _isRunning;

    internal TurnHeaderBlock(TurnHeaderEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.TurnHeader;

    public int Number { get; private set; }

    public string Who { get => _who; private set => SetProperty(ref _who, value); }

    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var header = (TurnHeaderEntry)entry;
        Number = header.Number;
        Who = string.Create(CultureInfo.InvariantCulture, $"corvid · turn {header.Number}");
        IsRunning = header.Running;
        Tick(context);
    }

    internal override void Tick(BlockContext context)
    {
        var header = (TurnHeaderEntry)Entry;
        var parts = new List<string>();
        if (!header.Models.IsDefaultOrEmpty)
            parts.Add(string.Join(" → ", header.Models));
        if (header.Tokens is { } tokens)
            parts.Add($"{SessionText.Tokens(tokens)} tokens");
        else if (header.Running && header.Step is { } step)
        {
            var text = header.MaxSteps is { } max ? $"step {step} of {max}" : $"step {step}";
            // A5: the step's generator, when the turn has moved off the one it started on.
            if (header.StepModel is { Length: > 0 } on && !header.Models.IsDefaultOrEmpty && on != header.Models[0])
                text += $" on {on}";
            parts.Add(text);
        }
        var elapsed = header.Running ? context.Now - header.StartedAt : header.Elapsed ?? TimeSpan.Zero;
        parts.Add(SessionText.Duration(elapsed));
        if (header.Cost is { } cost)
            parts.Add(SessionText.Cost(cost));
        var ending = header.Outcome switch
        {
            TurnOutcome.Cancelled => "interrupted",
            TurnOutcome.Failed => header.Error is { Length: > 0 } error ? $"failed: {error}" : "failed",
            TurnOutcome.Stuck => "stuck",
            TurnOutcome.StepBudgetExhausted => "step budget used up",
            _ => null,
        };
        if (ending is not null)
            parts.Add(ending);
        Detail = string.Join(" · ", parts);
    }
}

/// <summary>One decision as a chip: the question in bold, then "0.94 → task"; an unsure one says so.</summary>
/// <param name="Question">The question, drawn bold ("intent").</param>
/// <param name="Text">The rest: "0.94 → task", "3 (0.86) → ask", "0.41 → no, unsure".</param>
public sealed record DecisionChip(string Question, string Text, bool IsUnsure, bool IsAsk)
{
    public string AutomationName => $"{Question} {Text}";

    internal static DecisionChip From(DecisionAnswer answer)
    {
        var confidence = SessionText.Confidence(answer.Confidence);
        var score = answer.Option is { Length: > 0 } option ? $"{option} ({confidence})" : confidence;
        var text = $"{score} → {answer.Action}{(answer.Acted ? "" : ", unsure")}";
        return new DecisionChip(answer.Question, text, !answer.Acted, answer.Action == "ask");
    }
}

/// <summary>
/// A checkpoint's chips row. <see cref="IsSummary"/> rows sit under a turn's header; the others are at their place in the
/// turn and show with <see cref="SessionViewModel.ShowAllDecisions"/> (Ctrl+Shift+D).
/// </summary>
public sealed class DecisionsBlock : TranscriptBlock
{
    private IReadOnlyList<DecisionChip> _chips = [];
    private string _automationName = "";

    internal DecisionsBlock(DecisionsEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Decisions;

    public IReadOnlyList<DecisionChip> Chips { get => _chips; private set => SetProperty(ref _chips, value); }

    public bool IsSummary { get; private set; }

    /// <summary>"Ingress checkpoint, Kev-0.8B", as the mockup labels the row.</summary>
    public string AutomationName { get => _automationName; private set => SetProperty(ref _automationName, value); }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var decisions = (DecisionsEntry)entry;
        IsSummary = decisions.Summary;
        Chips = [.. decisions.Group.Answers.Select(DecisionChip.From)];
        var checkpoint = SessionText.Checkpoint(decisions.Group.Checkpoint);
        // An unnamed checkpoint (a feed that lost the span's tag) is still a row, not a failure to draw the session.
        AutomationName = checkpoint.Length == 0
            ? $"Checkpoint, {decisions.Group.Decider}"
            : $"{char.ToUpperInvariant(checkpoint[0])}{checkpoint[1..]} checkpoint, {decisions.Group.Decider}";
    }
}

/// <summary>
/// The generator changed: "escalated to <b>claude</b>: step budget (difficulty 0.71) · 2 values redacted · what left the
/// machine" (<c>.row.esc</c>, a cloud icon), or "kept local: the privacy gate refused the cloud model".
/// </summary>
public sealed class SwitchBlock : TranscriptBlock
{
    internal SwitchBlock(SwitchEntry entry, BlockContext context)
        : base(entry)
    {
        OpenEgressCommand = new RelayCommand(() => context.Session.Follow(AreaLinkTarget.Egress));
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Switch;

    public ModelSwitchKind SwitchKind { get; private set; }

    public IconKind Icon => IconKind.Cloud;

    /// <summary>"escalated to", "kept local", "fell back to", "switched to".</summary>
    public string Lead { get; private set; } = "";

    /// <summary>The model, drawn bold; empty for "kept local".</summary>
    public string Model { get; private set; } = "";

    /// <summary>Why, after a colon: "step budget (difficulty 0.71)".</summary>
    public string Reason { get; private set; } = "";

    /// <summary>"2 values redacted"; empty when nothing was.</summary>
    public string Redaction { get; private set; } = "";

    public bool HasRedaction => Redaction.Length > 0;

    /// <summary>Something left the machine: the "what left the machine" link opens the egress ledger in Audit (G-9).</summary>
    public bool HasEgressLink { get; private set; }

    public string EgressLinkText => "what left the machine";

    public RelayCommand OpenEgressCommand { get; }

    /// <summary>The whole row as one line, for a screen reader and the phone.</summary>
    public string Text { get; private set; } = "";

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var change = ((SwitchEntry)entry).Switch;
        SwitchKind = change.Kind;
        (Lead, Model) = change.Kind switch
        {
            ModelSwitchKind.Escalated => ("escalated to", change.To),
            // A5: a privacy retreat names the local model it moved to; a refused escalation stays where it was.
            ModelSwitchKind.KeptLocal when change.To.Length > 0 && change.To != change.From => ("kept local on", change.To),
            ModelSwitchKind.KeptLocal => ("kept local", ""),
            ModelSwitchKind.FellBack => ("fell back to", change.To),
            ModelSwitchKind.Returned => ("back to", change.To),
            _ => ("switched to", change.To),
        };
        Model = SafeText.Clean(Model);
        // A5: a row for another slot (the decider, per session) names it.
        if (change.Slot is { Length: > 0 } slot && slot != "generator")
            Lead = $"{SafeText.Clean(slot)} {Lead}";
        Reason = SafeText.Clean(change.Reason);
        Redaction = change.Redacted is > 0 and var redacted ? $"{SessionText.Plural(redacted, "value")} redacted" : "";
        HasEgressLink = change.Cloud && change.Kind != ModelSwitchKind.KeptLocal;
        var text = Model.Length > 0 ? $"{Lead} {Model}" : Lead;
        if (Reason.Length > 0)
            text += $": {Reason}";
        if (HasRedaction)
            text += $" · {Redaction}";
        Text = text;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Taint started ("outside content in context: web_fetch docs.example.org · network reads now ask…") or cleared.</summary>
public sealed class TaintBlock : TranscriptBlock
{
    internal TaintBlock(TaintEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Taint;

    public IconKind Icon => IconKind.Warning;

    public bool IsCleared { get; private set; }

    /// <summary>The bold lead: "outside content in context" or "outside content cleared".</summary>
    public string Lead { get; private set; } = "";

    /// <summary>The rest, after a colon.</summary>
    public string Text { get; private set; } = "";

    public string AutomationName => $"{Lead}: {Text}";

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var taint = (TaintEntry)entry;
        IsCleared = !taint.Tainted;
        if (taint.Tainted)
        {
            Lead = "outside content in context";
            const string Effect = "network reads now ask; egress and new recipients offer only \"once\"";
            Text = taint.Source is { Length: > 0 } source ? $"{SafeText.Clean(source)} · {Effect}" : Effect;
        }
        else
        {
            Lead = "outside content cleared";
            Text = "network reads and egress follow the approval mode again";
        }

        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Answer text, parsed as markdown each time it grows; <see cref="IsStreaming"/> draws the caret after it.</summary>
public sealed class AnswerBlock : TranscriptBlock
{
    private MarkdownDocument _document = MarkdownDocument.Empty;
    private bool _isStreaming;

    internal AnswerBlock(AnswerEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Answer;

    public MarkdownDocument Document { get => _document; private set => SetProperty(ref _document, value); }

    /// <summary>The blocks to draw, in order.</summary>
    public IReadOnlyList<MarkdownBlock> Blocks => _document.Blocks;

    /// <summary>The answer as the model wrote it: what copy and quote use.</summary>
    public string Text => _document.Source;

    public bool IsStreaming { get => _isStreaming; private set => SetProperty(ref _isStreaming, value); }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var answer = (AnswerEntry)entry;
        if (!string.Equals(answer.Text, _document.Source, StringComparison.Ordinal))
        {
            Document = MarkdownDocument.Parse(answer.Text);
            OnPropertyChanged(nameof(Blocks));
            OnPropertyChanged(nameof(Text));
        }

        IsStreaming = answer.Streaming;
    }
}

/// <summary>The turn's reasoning: "thought for 4 s" (a link button), expandable to the text (K-1).</summary>
public sealed class ThoughtBlock : TranscriptBlock
{
    private string _label = "";
    private string _text = "";
    private bool _isExpanded;
    private bool _isStreaming;

    internal ThoughtBlock(ThoughtEntry entry, BlockContext context)
        : base(entry)
    {
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Thought;

    /// <summary>"thought for 4 s", or "thinking · 2 s" while it streams.</summary>
    public string Label { get => _label; private set => SetProperty(ref _label, value); }

    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    public bool IsStreaming { get => _isStreaming; private set => SetProperty(ref _isStreaming, value); }

    /// <summary>Ctrl+T, or a click on the label.</summary>
    public RelayCommand ToggleCommand { get; }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        Text = SafeText.Clean(((ThoughtEntry)entry).Text);
        Tick(context);
    }

    internal override void Tick(BlockContext context)
    {
        var thought = (ThoughtEntry)Entry;
        var time = thought.Thinking + (thought.PhaseStartedAt is { } started && context.Now > started ? context.Now - started : TimeSpan.Zero);
        IsStreaming = thought.Streaming;
        Label = thought.Streaming ? $"thinking · {SessionText.Duration(time)}" : $"thought for {SessionText.Duration(time)}";
    }
}

/// <summary>A notice row: staged memory with "review", a held MCP tool with "diff", a finished job with "open the run".</summary>
public sealed class NoticeBlock : TranscriptBlock
{
    private readonly BlockContext _context;

    internal NoticeBlock(NoticeEntry entry, BlockContext context)
        : base(entry)
    {
        _context = context;
        ActionCommand = new RelayCommand(Act, () => HasAction);
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Notice;

    public NoticeKind NoticeKind { get; private set; }

    public IconKind Icon { get; private set; }

    public Tone Tone { get; private set; }

    public string Text { get; private set; } = "";

    public string Detail { get; private set; } = "";

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The row's button: "review", "diff", "open the run"; empty when it has none.</summary>
    public string ActionLabel { get; private set; } = "";

    public bool HasAction => ActionLabel.Length > 0;

    public RelayCommand ActionCommand { get; }

    /// <summary>Staged memory draws a dotted border (docs/plan/ui.md §7).</summary>
    public bool IsMemory => NoticeKind == NoticeKind.MemoryStaged;

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var notice = ((NoticeEntry)entry).Notice;
        NoticeKind = notice.Kind;
        Text = SafeText.Clean(notice.Text);
        Detail = SafeText.Clean(notice.Detail);
        (Icon, Tone, ActionLabel) = notice.Kind switch
        {
            NoticeKind.MemoryStaged => (IconKind.Layers, Tone.Grey, "review"),
            NoticeKind.McpToolHeld => (IconKind.Plug, Tone.Red, "diff"),
            NoticeKind.McpToolAccepted => (IconKind.Plug, Tone.Grey, ""),
            NoticeKind.JobFinished => (IconKind.Clock, Tone.Violet, notice.Target is null ? "" : "open the run"),
            NoticeKind.Decider => (IconKind.Warning, Tone.Amber, ""),
            NoticeKind.Warning => (IconKind.Warning, Tone.Amber, ""),
            NoticeKind.Mode => (IconKind.Shield, Tone.Grey, ""),
            NoticeKind.Recap => (IconKind.Sparkle, Tone.Violet, ""),
            _ => (IconKind.None, Tone.Grey, ""),
        };
        OnPropertyChanged(string.Empty);
        ActionCommand.NotifyCanExecuteChanged();
    }

    private void Act()
    {
        var notice = ((NoticeEntry)Entry).Notice;
        switch (notice.Kind)
        {
            case NoticeKind.MemoryStaged:
                _context.Session.Navigate(Area.Memory);
                break;
            case NoticeKind.McpToolHeld:
                _context.Session.Navigate(Area.Mcp);
                break;
            case NoticeKind.JobFinished when notice.Target is { } session:
                _context.Session.OpenOtherSession(session);
                break;
        }
    }
}

/// <summary>A detection rule's match, in red, with its events and a link into Audit (docs/plan/ui.md F10).</summary>
public sealed class DetectionBlock : TranscriptBlock
{
    internal DetectionBlock(DetectionEntry entry, BlockContext context)
        : base(entry)
    {
        OpenInAuditCommand = new RelayCommand(() => context.Session.Follow(AreaLinkTarget.Detection,
            [.. _detection.Events.Select(match => match.Sequence)], _detection.RuleId));
        Apply(entry, context);
    }

    private DetectionInfo _detection = null!;

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Detection;

    public IconKind Icon => IconKind.Shield;

    /// <summary>"detection: secret-read-then-egress".</summary>
    public string Title { get; private set; } = "";

    /// <summary>"high severity · forced an ask", or "· recording only" when enforcement is off.</summary>
    public string Detail { get; private set; } = "";

    public string Reason { get; private set; } = "";

    /// <summary>The matched events: "#1203 read_file ~/.aws/credentials".</summary>
    public IReadOnlyList<string> Events { get; private set; } = [];

    public RelayCommand OpenInAuditCommand { get; }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var detection = ((DetectionEntry)entry).Detection;
        _detection = detection;
        Title = $"detection: {SafeText.Clean(detection.RuleId)}";
        Detail = DetectionText.Detail(detection);
        Reason = SafeText.Clean(detection.Reason);
        Events = DetectionText.Events(detection);
        OnPropertyChanged(string.Empty);
    }
}

internal static class DetectionText
{
    public static string Detail(DetectionInfo detection)
    {
        var posture = detection.Enforced ? "forced an ask"
            : detection.Posture == "detect" ? "recording only"
            : detection.Posture;
        return $"{detection.Severity} severity · {posture}";
    }

    public static IReadOnlyList<string> Events(DetectionInfo detection) =>
        [.. detection.Events.Select(match => string.Create(CultureInfo.InvariantCulture, $"#{match.Sequence} {SafeText.Clean(match.Summary)}"))];
}

/// <summary>A labelled line of a compaction record ("goal", "decisions").</summary>
public sealed record LabelledText(string Label, string Text);

/// <summary>A compaction marker that expands into the structured record (docs/plan/ui.md §4.2).</summary>
public sealed class CompactionBlock : TranscriptBlock
{
    private bool _isExpanded;

    internal CompactionBlock(CompactionEntry entry, BlockContext context)
        : base(entry)
    {
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Compaction;

    /// <summary>"compacted turns 1 to 8 · expanded on a larger window".</summary>
    public string Text { get; private set; } = "";

    public IReadOnlyList<LabelledText> Record { get; private set; } = [];

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

    public RelayCommand ToggleCommand { get; }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var compaction = ((CompactionEntry)entry).Compaction;
        var text = string.Create(CultureInfo.InvariantCulture, $"compacted turns {compaction.FromTurn} to {compaction.ThroughTurn}");
        if (compaction.Focus is { Length: > 0 } focus)
            text += $" · kept: {SafeText.Clean(focus)}";
        if (compaction.Expanded)
            text += " · expanded on a larger window";
        Text = text;
        var record = new List<LabelledText>();
        if (compaction.Goal is { Length: > 0 } goal)
            record.Add(new("goal", SafeText.Clean(goal)));
        Add(record, "constraints", compaction.Constraints);
        Add(record, "decisions", compaction.Decisions);
        Add(record, "files", compaction.Files);
        Add(record, "open questions", compaction.OpenQuestions);
        if (compaction.NextStep is { Length: > 0 } next)
            record.Add(new("next step", SafeText.Clean(next)));
        Record = record;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Record));

        static void Add(List<LabelledText> record, string label, System.Collections.Immutable.ImmutableArray<string> items)
        {
            if (!items.IsDefaultOrEmpty)
                record.Add(new(label, string.Join("; ", items.Select(SafeText.Clean))));
        }
    }
}

/// <summary>A path a restore touched, and what happened to it.</summary>
public sealed record RestoredPath(string Path, string Result);

/// <summary>A rewind marker: what went back, and the restore's result exactly as the kernel reports it.</summary>
public sealed class RewindBlock : TranscriptBlock
{
    internal RewindBlock(RewindEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Rewind;

    public IconKind Icon => IconKind.Clock;

    /// <summary>"rewound to before turn 10: files and conversation (rollback)".</summary>
    public string Text { get; private set; } = "";

    /// <summary>"restored 2 · merged 1 · refused 0 · restore checkpoint c-35".</summary>
    public string Detail { get; private set; } = "";

    public bool IsRefused { get; private set; }

    public IReadOnlyList<RestoredPath> Paths { get; private set; } = [];

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var rewind = ((RewindEntry)entry).Rewind;
        IsRefused = rewind.Refused;
        var scope = rewind.Scope switch
        {
            RewindScope.ConversationOnly => "the conversation only",
            RewindScope.FilesOnly => "the files only",
            _ => "files and conversation",
        };
        Text = rewind.Refused
            ? $"restore refused: {SafeText.Clean(rewind.Detail ?? "a conflict")}"
            : rewind.FromCallId is not null
                ? string.Create(CultureInfo.InvariantCulture, $"rewound to part-way through turn {rewind.Turn - 1}: {scope} ({rewind.Reason})")
                : string.Create(CultureInfo.InvariantCulture, $"rewound to before turn {rewind.Turn}: {scope} ({rewind.Reason})");
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"restored {rewind.Restored.Length} · merged {rewind.Merged.Length} · refused {rewind.Conflicts.Length}");
        if (rewind.CheckpointId is { } checkpoint)
            detail += $" · restore checkpoint {checkpoint}";
        Detail = detail;
        Paths =
        [
            .. rewind.Restored.Select(path => new RestoredPath(path, "restored")),
            .. rewind.Merged.Select(path => new RestoredPath(path, "merged with your edit")),
            .. rewind.Conflicts.Select(conflict => new RestoredPath(conflict.Path, $"refused: {SafeText.Clean(conflict.Reason)}")),
        ];
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>An option of a clarifying question, as a button.</summary>
public sealed class QuestionOption(string label, RelayCommand choose)
{
    public string Label { get; } = label;

    public RelayCommand Choose { get; } = choose;
}

/// <summary>
/// A clarifying question (K-6): options as buttons and a free-text answer through the composer, pinned until answered.
/// </summary>
public sealed class QuestionBlock : TranscriptBlock
{
    private readonly BlockContext _context;
    private bool _isOpen;
    private string _answer = "";

    internal QuestionBlock(QuestionEntry entry, BlockContext context)
        : base(entry)
    {
        _context = context;
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Question;

    public IconKind Icon => IconKind.Question;

    public string QuestionId { get; private set; } = "";

    /// <summary>"which version number?"; "Question" when it has no title (the question shows below it).</summary>
    public string Title { get; private set; } = "";

    public string Text { get; private set; } = "";

    public IReadOnlyList<QuestionOption> Options { get; private set; } = [];

    /// <summary>The composer's text answers it too.</summary>
    public bool AllowsFreeText { get; private set; }

    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    /// <summary>"answered: 0.2.0" once answered; empty while open or when the turn moved on unanswered.</summary>
    public string AnswerText { get => _answer; private set => SetProperty(ref _answer, value); }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var question = (QuestionEntry)entry;
        var asked = question.Question;
        if (QuestionId != asked.QuestionId || Options.Count != asked.Options.Length)
        {
            QuestionId = asked.QuestionId;
            // The model wrote the options, and a click sends one as the person's own message: its label shows every hidden
            // character (SafeText.Reveal) rather than dropping it, and what is sent is only the text a reader sees
            // (SafeText.Clean), so nothing the person did not see reaches the generator as their words. An option that is
            // all hidden characters cannot be chosen; the composer answers it.
            Options = [.. asked.Options.Select(option =>
            {
                var visible = SafeText.Clean(option).Trim();
                return new QuestionOption(SafeText.Reveal(option),
                    new RelayCommand(() => _context.Session.AnswerQuestion(asked.QuestionId, visible), () => IsOpen && visible.Length > 0));
            })];
        }

        Title = SafeText.Clean(asked.Title ?? "Question");
        Text = SafeText.Clean(asked.Text);
        AllowsFreeText = asked.AllowsFreeText;
        IsOpen = question.Answer is null;
        AnswerText = question.Answer is { Length: > 0 } answer ? $"answered: {SafeText.Clean(answer)}" : "";
        foreach (var option in Options)
            option.Choose.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(AllowsFreeText));
    }
}

/// <summary>A slash command's output: its title, lines and table, in a tone.</summary>
public sealed class CommandBlock : TranscriptBlock
{
    internal CommandBlock(CommandEntry entry, BlockContext context)
        : base(entry) => Apply(entry, context);

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Command;

    public string Line { get; private set; } = "";

    public string Title { get; private set; } = "";

    public bool HasTitle => Title.Length > 0;

    public IReadOnlyList<string> Lines { get; private set; } = [];

    public IReadOnlyList<string> Columns { get; private set; } = [];

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; private set; } = [];

    public bool HasTable => Columns.Count > 0;

    public OutputTone Tone { get; private set; }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var result = ((CommandEntry)entry).Result;
        Line = SafeText.Clean(result.Line);
        Title = SafeText.Clean(result.Title);
        Lines = [.. result.Lines.Select(SafeText.Clean)];
        Columns = [.. result.Columns.Select(SafeText.Clean)];
        Rows = [.. result.Rows.Select(row => (IReadOnlyList<string>)[.. row.Select(SafeText.Clean)])];
        Tone = result.Tone;
        OnPropertyChanged(string.Empty);
    }
}
