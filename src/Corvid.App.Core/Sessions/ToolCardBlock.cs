using System.Globalization;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

/// <summary>What a tool card shows about its call's progress, for its look.</summary>
public enum ToolCardState
{
    Pending,
    /// <summary>The amber "awaiting approval" tag.</summary>
    AwaitingApproval,
    /// <summary>The accent border, the live tail and Stop.</summary>
    Running,
    Succeeded,
    Failed,
    Denied,
    Cancelled,
}

/// <summary>
/// A file edit as the transcript and the approval card draw it: the path, the counts, and the unified rows with word
/// highlights, computed when first drawn.
/// </summary>
public sealed class FileDiffView
{
    private readonly Lazy<FileDiff> _diff;
    private IReadOnlyList<DiffRow>? _unified;
    private IReadOnlyList<SideBySideRow>? _sideBySide;

    public FileDiffView(FileEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Edit = edit;
        Path = SafeText.Reveal(edit.Path);
        _diff = new Lazy<FileDiff>(() => FileDiff.Compute(edit.Path, edit.OldText, edit.NewText));
    }

    public FileEdit Edit { get; }

    public string Path { get; }

    public FileDiff Diff => _diff.Value;

    /// <summary>"+12" and "−3", drawn green and red.</summary>
    public string AddedText => string.Create(CultureInfo.InvariantCulture, $"+{Diff.Added}");

    public string RemovedText => string.Create(CultureInfo.InvariantCulture, $"−{Diff.Removed}");

    public bool IsNewFile => Edit.OldText is null;

    public IReadOnlyList<DiffRow> Rows => _unified ??= Diff.Unified();

    public IReadOnlyList<SideBySideRow> SideBySide => _sideBySide ??= Diff.SideBySide();
}

/// <summary>
/// A tool call's card (Main.png <c>.card</c>): the kind's icon, the tool's name and its target in monospace, a few words
/// on the result ("· 3 hits"), and on the right its state ("auto: read-only · 0.3 s", "running · 12 s", the amber
/// "awaiting approval"). A side-effecting call says why it ran (<see cref="Why"/>). While it runs it shows its output's
/// tail and Stop; expanded, its full output or its diff.
/// </summary>
public sealed class ToolCardBlock : TranscriptBlock
{
    private readonly BlockContext _context;
    private ToolCardState _state;
    private string _statusText = "";
    private string _target = "";
    private string _summary = "";
    private IReadOnlyList<MarkdownRun> _why = [];
    private string _tail = "";
    private string _output = "";
    private bool _isExpanded;
    private IReadOnlyList<FileDiffView> _diffs = [];
    private string _automationName = "";

    internal ToolCardBlock(ToolEntry entry, BlockContext context)
        : base(entry)
    {
        _context = context;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded, () => CanExpand);
        StopCommand = new RelayCommand(() => _context.Session.Interrupt(), () => IsRunning);
        ShowOutputCommand = new RelayCommand(() => _context.Session.ShowOutput(this), () => CanExpand);
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Tool;

    public string CallId { get; private set; } = "";

    public ToolKind ToolKind { get; private set; }

    public IconKind Icon { get; private set; }

    /// <summary>"read_file", "shell".</summary>
    public string ToolName { get; private set; } = "";

    /// <summary>What it acts on, in monospace: ".github/workflows/ci.yml".</summary>
    public string Target { get => _target; private set => SetProperty(ref _target, value); }

    /// <summary>A few words on the result, drawn after the target with a middle dot: "3 hits", "48 KB".</summary>
    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
                OnPropertyChanged(nameof(HasSummary));
        }
    }

    public bool HasSummary => _summary.Length > 0;

    public ToolCardState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsAwaitingApproval));
            OnPropertyChanged(nameof(HasTail));
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsRunning => _state == ToolCardState.Running;

    /// <summary>Drawn as the amber "awaiting approval" tag in place of <see cref="StatusText"/>.</summary>
    public bool IsAwaitingApproval => _state == ToolCardState.AwaitingApproval;

    /// <summary>The right-hand state: "auto: read-only · 0.3 s", "approved once · 1.9 s", "running · 12 s".</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>
    /// Why it ran, or did not, under the head (<c>.card-why</c>): "ran under a session rule: `dotnet test` in workspace
    /// (approval a-31, turn 10) · risk 1 (0.93) · checkpoint c-44 taken before the call". Code runs are monospace.
    /// </summary>
    public IReadOnlyList<MarkdownRun> Why
    {
        get => _why;
        private set
        {
            if (SetProperty(ref _why, value))
                OnPropertyChanged(nameof(HasWhy));
        }
    }

    public bool HasWhy => _why.Count > 0;

    /// <summary>The last lines of output while it runs (<c>pre.tail</c>, K-2).</summary>
    public string Tail
    {
        get => _tail;
        private set
        {
            if (SetProperty(ref _tail, value))
                OnPropertyChanged(nameof(HasTail));
        }
    }

    public bool HasTail => IsRunning && _tail.Length > 0;

    /// <summary>Lines of a running tail the card shows: what its pane holds, the newest (K-2). The whole output is in Output.</summary>
    internal const int TailLines = 5;

    /// <summary>The last <paramref name="count"/> lines of <paramref name="text"/>.</summary>
    internal static string LastLines(string text, int count)
    {
        var end = text.Length;
        for (var seen = 0; end > 0; end--)
            if (text[end - 1] == '\n' && ++seen == count)
                return text[end..];
        return text;
    }

    /// <summary>The full output, shown when expanded and in the Output panel.</summary>
    public string Output { get => _output; private set => SetProperty(ref _output, value); }

    /// <summary>The edits a <c>write_file</c> or <c>patch_file</c> made, from the write ledger.</summary>
    public IReadOnlyList<FileDiffView> Diffs
    {
        get => _diffs;
        private set
        {
            if (SetProperty(ref _diffs, value))
                OnPropertyChanged(nameof(HasDiffs));
        }
    }

    public bool HasDiffs => _diffs.Count > 0;

    public bool CanExpand => _output.Length > 0 || _diffs.Count > 0;

    /// <summary>The chevron's state (Ctrl+O expands the latest card).</summary>
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value && CanExpand); }

    public RelayCommand ToggleCommand { get; }

    /// <summary>Stop on a running card: interrupts the turn, which keeps the work so far (a single call cannot be stopped yet).</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>Shows the full output in the dock's Output panel.</summary>
    public RelayCommand ShowOutputCommand { get; }

    public string AutomationName { get => _automationName; private set => SetProperty(ref _automationName, value); }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var tool = (ToolEntry)entry;
        CallId = tool.CallId;
        ToolKind = tool.Kind;
        Icon = IconFor(tool);
        ToolName = SafeText.Clean(tool.Tool);
        Target = SafeText.Clean(tool.Target);
        Summary = SafeText.Clean(tool.Summary);
        State = StateOf(tool);
        Tail = LastLines(SafeText.Clean(tool.Tail).TrimEnd('\n'), TailLines);
        Output = SafeText.Clean(tool.Output);
        if (Diffs.Count != tool.Diffs.Length || !Diffs.Select(diff => diff.Edit).SequenceEqual(tool.Diffs))
            Diffs = [.. tool.Diffs.Select(edit => new FileDiffView(edit))];
        Why = SessionText.Runs(WhyText(tool.Verdict));
        OnPropertyChanged(nameof(CanExpand));
        ToggleCommand.NotifyCanExecuteChanged();
        ShowOutputCommand.NotifyCanExecuteChanged();
        Tick(context);
    }

    internal override void Tick(BlockContext context)
    {
        var tool = (ToolEntry)Entry;
        StatusText = Status(tool, context.Now);
        AutomationName = $"{ToolName} {Target}{(HasSummary ? $", {Summary}" : "")}, {(IsAwaitingApproval ? "awaiting approval" : StatusText)}";
    }

    private static ToolCardState StateOf(ToolEntry tool) => tool switch
    {
        { ApprovalId: not null } => ToolCardState.AwaitingApproval,
        { Status: ToolCallStatus.Pending } => ToolCardState.Pending,
        { Status: ToolCallStatus.InProgress } => ToolCardState.Running,
        { Outcome: ToolOutcome.Denied } => ToolCardState.Denied,
        { Outcome: ToolOutcome.Cancelled } => ToolCardState.Cancelled,
        { Status: ToolCallStatus.Failed } or { Outcome: ToolOutcome.Failed or ToolOutcome.InvalidArguments } => ToolCardState.Failed,
        _ => ToolCardState.Succeeded,
    };

    private static IconKind IconFor(ToolEntry tool) => tool.Kind switch
    {
        ToolKind.Execute => IconKind.Terminal,
        ToolKind.Search => IconKind.Search,
        ToolKind.Fetch => IconKind.Globe,
        ToolKind.Memory => IconKind.Layers,
        ToolKind.Mcp => IconKind.Plug,
        ToolKind.Read or ToolKind.Edit or ToolKind.Delete => IconKind.File,
        _ => IconKind.Sparkle,
    };

    private static string Status(ToolEntry tool, DateTimeOffset now)
    {
        switch (StateOf(tool))
        {
            case ToolCardState.AwaitingApproval:
                return "awaiting approval";
            case ToolCardState.Pending:
                return "pending";
            case ToolCardState.Running:
                return $"running · {SessionText.Duration(now - tool.StartedAt)}";
        }

        var parts = new List<string>();
        if (VerdictShort(tool.Verdict) is { } verdict)
            parts.Add(verdict);
        var outcome = tool.Outcome switch
        {
            ToolOutcome.Failed => "failed",
            ToolOutcome.InvalidArguments => "invalid arguments",
            ToolOutcome.Cancelled when tool.Verdict?.Kind is not VerdictKind.Cancelled => "cancelled",
            ToolOutcome.Denied when tool.Verdict?.Kind is not (VerdictKind.Denied or VerdictKind.TimedOut or VerdictKind.RuleDenied or VerdictKind.Hardline
                or VerdictKind.NoApprover) => "denied",
            null when tool.Status == ToolCallStatus.Failed => "failed",
            _ => null,
        };
        if (outcome is not null)
            parts.Add(outcome);
        if (parts.Count == 0)
            parts.Add("done");
        if (tool.Elapsed is { } elapsed)
            parts.Add(SessionText.Precise(elapsed));
        return string.Join(" · ", parts);
    }

    /// <summary>The verdict in the card's right-hand words.</summary>
    internal static string? VerdictShort(CallVerdict? verdict) => verdict?.Kind switch
    {
        VerdictKind.ReadOnly => "auto: read-only",
        VerdictKind.Mode => $"auto: {(verdict.Mode is { } mode ? SessionText.Mode(mode) : "smart")} mode",
        VerdictKind.SessionRule => "session rule",
        VerdictKind.AlwaysRule => "always rule",
        VerdictKind.ApprovedOnce => "approved once",
        VerdictKind.ApprovedSession => "approved for this session",
        VerdictKind.ApprovedAlways => "approved always",
        VerdictKind.Denied => "denied",
        VerdictKind.RuleDenied => "denied by a rule",
        VerdictKind.Hardline => "refused: hardline",
        VerdictKind.TimedOut => "denied: deadline",
        VerdictKind.Cancelled => "denied: cancelled",
        VerdictKind.NoApprover => "denied: nobody to ask",
        _ => null,
    };

    /// <summary>The why line, with the rule in backticks: empty for verdicts the right-hand words already say.</summary>
    internal static string? WhyText(CallVerdict? verdict)
    {
        if (verdict is null)
            return null;
        var risk = verdict.RiskLevel is { } level
            ? verdict.RiskConfidence is { } confidence
                ? $"risk {level} ({SessionText.Confidence(confidence)})"
                : $"risk {level}"
            : null;
        var checkpoint = verdict.CheckpointId is { } id ? $"checkpoint {id} taken before the call" : null;
        var source = verdict.ApprovalId is { } approval
            ? verdict.RuleTurn is { } turn ? $" (approval {approval}, turn {turn.ToString(CultureInfo.InvariantCulture)})" : $" (approval {approval})"
            : "";
        string? lead = verdict.Kind switch
        {
            VerdictKind.SessionRule => $"ran under a session rule: {verdict.Rule ?? "a remembered approval"}{source}",
            VerdictKind.AlwaysRule => $"ran under an always rule: {verdict.Rule ?? "a remembered approval"}{source}",
            VerdictKind.Mode => $"ran unasked in {(verdict.Mode is { } mode ? SessionText.Mode(mode) : "smart")} mode"
                + (verdict.Detail is { Length: > 0 } detail ? $", {detail}" : ""),
            VerdictKind.ApprovedSession => verdict.Rule is { } rule ? $"approved for this session (rule: {rule})" : "approved for this session",
            VerdictKind.ApprovedAlways => verdict.Rule is { } always ? $"approved always (rule: {always})" : "approved always",
            VerdictKind.Denied => verdict.Detail is { Length: > 0 } reason ? $"denied by you: {reason}" : null,
            VerdictKind.RuleDenied => $"denied by a remembered rule{(verdict.Detail is { Length: > 0 } rd ? $": {rd}" : "")}",
            VerdictKind.Hardline => $"refused by the hardline list{(verdict.Detail is { Length: > 0 } hd ? $": {hd}" : "")}",
            VerdictKind.NoApprover => $"needed an approval and nobody could answer{(verdict.Detail is { Length: > 0 } na ? $": {na}" : "")}",
            _ => null,
        };
        if (lead is null)
            return null;
        var parts = new List<string> { lead };
        if (verdict.Kind is VerdictKind.SessionRule or VerdictKind.AlwaysRule or VerdictKind.Mode && risk is not null)
            parts.Add(risk);
        if (verdict.Kind is not (VerdictKind.Denied or VerdictKind.RuleDenied or VerdictKind.Hardline or VerdictKind.NoApprover) && checkpoint is not null)
            parts.Add(checkpoint);
        return string.Join(" · ", parts);
    }
}
