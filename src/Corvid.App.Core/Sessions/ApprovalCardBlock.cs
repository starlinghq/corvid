using System.Collections.Immutable;
using System.Globalization;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// A target chip under a parsed command: "<c>./build/</c> · workspace · 212 files", red for a delete; or "writes under
/// <c>bin/ obj/ TestResults/</c>" for one the command reaches by convention.
/// </summary>
/// <param name="Prefix">"writes under" for an implied target; empty otherwise.</param>
/// <param name="Value">The target, in monospace.</param>
/// <param name="Detail">Where it is and what the engine adds, after a middle dot: "workspace · 212 files".</param>
public sealed record TargetChip(string Prefix, string Value, string Detail, bool IsDestructive)
{
    public bool HasPrefix => Prefix.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    public string Text => (HasPrefix ? Prefix + " " : "") + Value + (HasDetail ? " · " + Detail : "");
}

/// <summary>A labelled line under a parsed command ("undo", "network"), with code runs.</summary>
public sealed record CommandNoteView(string Label, IReadOnlyList<MarkdownRun> Runs);

/// <summary>
/// One parsed command of the action (<c>.parsed-cmd</c>): its text with its effects as a tag ("delete, recursive" in red,
/// "execute" in grey), the verb, subcommand and flags, the targets and the notes.
/// </summary>
public sealed record ParsedCommandView(
    string Text,
    string EffectsText,
    Tone EffectsTone,
    string Verb,
    string Subcommand,
    string Flags,
    IReadOnlyList<TargetChip> Targets,
    IReadOnlyList<CommandNoteView> Notes)
{
    public bool HasSubcommand => Subcommand.Length > 0;

    public bool HasFlags => Flags.Length > 0;

    public bool HasEffects => EffectsText.Length > 0;

    internal static ParsedCommandView From(ParsedCommand command)
    {
        var effects = command.Effects.Where(effect => effect != "read").ToList();
        var dangerous = effects.Any(effect => effect is "delete" or "network" or "elevated" or "system" or "signal" or "configuration");
        return new ParsedCommandView(
            SafeText.Reveal(command.Text),
            string.Join(", ", effects),
            dangerous ? Tone.Red : Tone.Grey,
            SafeText.Reveal(command.Verb),
            SafeText.Reveal(command.Subcommand),
            string.Join(" ", command.Flags.Select(flag => SafeText.Reveal(flag))),
            [.. command.Targets.Select(Chip)],
            [.. command.Notes.Select(note => new CommandNoteView(note.Label, SessionText.Runs(note.Text)))]);
    }

    private static TargetChip Chip(CommandTarget target)
    {
        var where = target.Location switch
        {
            "inside" => "workspace",
            "workspace_root" => "workspace root",
            "outside" => "outside the workspace",
            "unresolved" => "not known before it runs",
            _ => null,
        };
        var detail = string.Join(" · ", new[] { where, SafeText.Clean(target.Detail) }.Where(part => !string.IsNullOrEmpty(part)));
        return new TargetChip(target.Implied ? $"{target.Access}s under" : "", SafeText.Reveal(target.Value), detail, target.Access == "delete");
    }
}

/// <summary>A reason row (<c>.reasons li</c>): its short code ("risk 3", "taint") and the sentence, with code runs.</summary>
public sealed record ReasonView(string Label, IReadOnlyList<MarkdownRun> Runs, IReadOnlyList<MarkdownRun> ShortRuns)
{
    public string Text => SessionText.Plain(Runs);
}

/// <summary>What an answer option does.</summary>
public enum ApprovalOptionKind
{
    Once,
    Session,
    /// <summary>Opens the rule preview first; the preview's button answers.</summary>
    Always,
    Deny,
    /// <summary>Opens a reason field first; sending it denies, and the reason goes as the next message.</summary>
    DenyWithReason,
}

/// <summary>
/// An answer button (<c>.opt</c>): its digit, its label ("Always…"; the phone's longer "Always, after a rule preview…"),
/// whether it denies, and whether it is the focused one or the one whose preview is open.
/// </summary>
public sealed class ApprovalOptionView : ObservableObject
{
    private bool _isFocused;
    private bool _isExpanded;

    internal ApprovalOptionView(ApprovalOptionKind kind, string key, string label, string longLabel, RelayCommand choose)
    {
        OptionKind = kind;
        Key = key;
        Label = label;
        LongLabel = longLabel;
        Choose = choose;
    }

    public ApprovalOptionKind OptionKind { get; }

    /// <summary>The digit that chooses it while the card has focus: 1 once, 2 session, 3 always, 4 deny, 5 deny and say why.</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>The phone's label.</summary>
    public string LongLabel { get; }

    public bool IsDeny => OptionKind is ApprovalOptionKind.Deny or ApprovalOptionKind.DenyWithReason;

    public RelayCommand Choose { get; }

    /// <summary>The option Enter chooses while the card has focus.</summary>
    public bool IsFocused { get => _isFocused; internal set => SetProperty(ref _isFocused, value); }

    /// <summary>Its preview or reason field is open (<c>aria-expanded</c>).</summary>
    public bool IsExpanded { get => _isExpanded; internal set => SetProperty(ref _isExpanded, value); }

    public string AutomationName => $"{Key}, {LongLabel}";
}

/// <summary>
/// A rule the "always" answer would write (<c>.rule-row</c>): "allow <c>rm -r</c> under paths [build/] narrow it before
/// saving", or "allow <c>dotnet test</c> in the workspace, no network, no elevation". Paths can be narrowed, never
/// widened (G-11).
/// </summary>
public sealed class RuleRowView : ObservableObject
{
    private readonly ImmutableArray<string> _original;
    private string _paths;
    private string _error = "";

    internal RuleRowView(RulePreview rule)
    {
        Decision = rule.Decision;
        Command = SafeText.Reveal(rule.Command);
        Scope = SafeText.Clean(rule.Scope);
        _original = rule.Paths;
        _paths = string.Join(" ", rule.Paths.Select(path => SafeText.Reveal(path)));
        // A path or command with a character the reader cannot see would be saved as something other than what is
        // shown, so such a rule is refused outright; "once" and "session" still answer the call.
        _hidden = SafeText.HasHidden(rule.Command) || rule.Paths.Any(SafeText.HasHidden);
        if (_hidden)
            _error = HiddenError;
    }

    private const string HiddenError = "this rule holds characters you cannot see (shown as ⟨U+…⟩), so it cannot be written";

    private readonly bool _hidden;

    /// <summary>"allow" (a green tag) or "deny".</summary>
    public string Decision { get; }

    public bool IsAllow => Decision == "allow";

    /// <summary>The verb with its subcommand or flags, in monospace: "rm -r".</summary>
    public string Command { get; }

    /// <summary>The scope in words, for a rule with no paths.</summary>
    public string Scope { get; }

    public bool HasPaths => !_original.IsDefaultOrEmpty;

    public string PathsLabel => "under paths";

    public string PathsHint => "narrow it before saving";

    /// <summary>The paths, separated by spaces, as the user edits them.</summary>
    public string Paths
    {
        get => _paths;
        set
        {
            if (SetProperty(ref _paths, value ?? ""))
                Validate();
        }
    }

    /// <summary>Why the edited paths cannot be written: one is not at or under the original paths.</summary>
    public string PathsError
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasPathsError));
        }
    }

    public bool HasPathsError => _error.Length > 0;

    internal ImmutableArray<string> EditedPaths() =>
        [.. _paths.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void Validate()
    {
        if (_hidden)
        {
            PathsError = HiddenError;
            return;
        }

        var edited = EditedPaths();
        if (edited.IsEmpty)
        {
            PathsError = "name at least one path";
            return;
        }

        var wider = edited.FirstOrDefault(path => !_original.Any(original => Covers(original, path)));
        PathsError = wider is null ? "" : $"{wider} is not under {string.Join(" or ", _original)}; a rule can only be narrowed";
    }

    private static bool Covers(string original, string path)
    {
        var root = original.TrimEnd('/');
        var candidate = path.TrimEnd('/');
        return candidate == root || candidate.StartsWith(root + "/", StringComparison.Ordinal) || root.Length == 0;
    }
}

/// <summary>The "always" answer's preview (<c>.rule-preview</c>): the rules it writes, from which approval, and where they are listed.</summary>
public sealed class RulePreviewView
{
    internal RulePreviewView(string header, string source, IReadOnlyList<RuleRowView> rows)
    {
        Header = header;
        Source = source;
        Rows = rows;
    }

    /// <summary>"Rule to be written · scope: always".</summary>
    public string Header { get; }

    /// <summary>"source: approval a-32, session cli-9f2e".</summary>
    public string Source { get; }

    public IReadOnlyList<RuleRowView> Rows { get; }

    public string Note => "Listed in Safety → Rules, removable there. A hardline denial or a detection match still asks whatever the rule says.";

    public string ConfirmLabel => "Write the rule and allow";

    public string BackLabel => "Back";
}

/// <summary>A detection match inside an approval card: the red block with the rule and its events.</summary>
public sealed record DetectionView(string Title, string Detail, string Reason, IReadOnlyList<string> Events);

/// <summary>
/// The open approval card (Approval.png <c>section.approval</c>, docs/plan/ui.md §4.4, F3): the command, each parsed
/// command with its verb, flags, effects and targets, the reasons with their codes, the answers as numbered buttons with
/// exactly the scopes the pipeline offers, the rule preview for "always", the deadline counting down, and the next prompt
/// in the queue.
/// <para>
/// Keys: a card that opens while the user types never takes their keys. Enter in the composer focuses the card first
/// (<see cref="SessionViewModel.SubmitCommand"/>); then <see cref="ChooseCommand"/> with a digit, or
/// <see cref="ConfirmCommand"/> (Enter) for the focused option, answers it. Digits answer only a focused card.
/// </para>
/// </summary>
public sealed class ApprovalCardBlock : TranscriptBlock
{
    private readonly BlockContext _context;
    private ApprovalRequested _request;
    private string _title = "";
    private string _phoneTitle = "";
    private string _deadlineText = "";
    private string _countdown = "";
    private IReadOnlyList<MarkdownRun> _next = [];
    private string _nextShort = "";
    private bool _isFocused;
    private bool _isRulePreviewOpen;
    private bool _isAskingWhy;
    private string _denyReason = "";
    private bool _isAnswering;
    private int _focusedIndex;
    private string _automationName = "";

    internal ApprovalCardBlock(ApprovalEntry entry, BlockContext context)
        : base(entry)
    {
        _context = context;
        _request = entry.Request;
        ChooseCommand = new RelayCommand<string>(Choose, key => _isFocused && !_isAnswering && Options.Any(option => option.Key == key));
        ConfirmCommand = new RelayCommand(Confirm, () => _isFocused && !_isAnswering);
        ConfirmRuleCommand = new RelayCommand(ConfirmRule, () => _isRulePreviewOpen && !_isAnswering && RulePreview?.Rows.All(row => !row.HasPathsError) != false);
        SendDenyReasonCommand = new RelayCommand(SendDenyReason, () => _isAskingWhy && !_isAnswering && _denyReason.Trim().Length > 0);
        BackCommand = new RelayCommand(Back, () => _isRulePreviewOpen || _isAskingWhy);
        Build(entry);
        Apply(entry, context);
    }

    public override TranscriptBlockKind Kind => TranscriptBlockKind.Approval;

    public string ApprovalId => _request.ApprovalId;

    public string Tool => _request.Tool;

    /// <summary>"Approval 1 of 2 · shell" (one prompt: "Approval · shell").</summary>
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    /// <summary>The phone's "Approval 1 of 2".</summary>
    public string PhoneTitle { get => _phoneTitle; private set => SetProperty(ref _phoneTitle, value); }

    /// <summary>"denied automatically in 4:12".</summary>
    public string DeadlineText { get => _deadlineText; private set => SetProperty(ref _deadlineText, value); }

    /// <summary>"4:12".</summary>
    public string Countdown { get => _countdown; private set => SetProperty(ref _countdown, value); }

    /// <summary>The phone's "4:12 left".</summary>
    public string ShortDeadline => $"{_countdown} left";

    /// <summary>The action as one line, in monospace (<c>.cmd</c>).</summary>
    public string CommandText { get; private set; } = "";

    /// <summary>The parser's reason when the command is outside the parsed subset; empty otherwise.</summary>
    public string Unparsed { get; private set; } = "";

    public IReadOnlyList<ParsedCommandView> Commands { get; private set; } = [];

    public IReadOnlyList<ReasonView> Reasons { get; private set; } = [];

    /// <summary>The answers offered, in key order; only the scopes the pipeline offers appear.</summary>
    public IReadOnlyList<ApprovalOptionView> Options { get; private set; } = [];

    /// <summary>The rules "always" would write; null when always is not offered.</summary>
    public RulePreviewView? RulePreview { get; private set; }

    public bool IsRulePreviewOpen
    {
        get => _isRulePreviewOpen;
        private set
        {
            if (SetProperty(ref _isRulePreviewOpen, value))
                RefreshCommands();
        }
    }

    /// <summary>The "deny and say why" field is open.</summary>
    public bool IsAskingWhy
    {
        get => _isAskingWhy;
        private set
        {
            if (SetProperty(ref _isAskingWhy, value))
                RefreshCommands();
        }
    }

    /// <summary>The reason a denial gives; sent as the next message.</summary>
    public string DenyReason
    {
        get => _denyReason;
        set
        {
            if (SetProperty(ref _denyReason, value ?? ""))
                SendDenyReasonCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The edit a <c>write_file</c> or <c>patch_file</c> call would make; null otherwise.</summary>
    public FileDiffView? ProposedDiff { get; private set; }

    public bool HasProposedDiff => ProposedDiff is not null;

    public IReadOnlyList<DetectionView> Detections { get; private set; } = [];

    public bool HasDetections => Detections.Count > 0;

    /// <summary>"MCP server notes · trust: untrusted"; empty for other tools.</summary>
    public string McpText { get; private set; } = "";

    /// <summary>"Next in the queue: `patch_file src/Corvid.Sandbox/BubblewrapSandbox.cs` (diff ready) · 2 of 2".</summary>
    public IReadOnlyList<MarkdownRun> NextText
    {
        get => _next;
        private set
        {
            if (SetProperty(ref _next, value))
                OnPropertyChanged(nameof(HasNext));
        }
    }

    public bool HasNext => _next.Count > 0;

    /// <summary>The phone's "Next in the queue: patch_file BubblewrapSandbox.cs (diff ready)".</summary>
    public string NextShort { get => _nextShort; private set => SetProperty(ref _nextShort, value); }

    /// <summary>The card has keyboard focus: digits and Enter answer it. Set by the first Enter in the composer.</summary>
    public bool IsFocused
    {
        get => _isFocused;
        internal set
        {
            if (!SetProperty(ref _isFocused, value))
                return;
            UpdateFocus();
            RefreshCommands();
        }
    }

    /// <summary>The option Enter chooses.</summary>
    public ApprovalOptionView? FocusedOption => _focusedIndex < Options.Count ? Options[_focusedIndex] : null;

    /// <summary>An answer is on its way to the engine; the buttons wait.</summary>
    public bool IsAnswering
    {
        get => _isAnswering;
        private set
        {
            if (SetProperty(ref _isAnswering, value))
                RefreshCommands();
        }
    }

    /// <summary>Chooses the option with this digit (1 to 5), when the card has focus.</summary>
    public RelayCommand<string> ChooseCommand { get; }

    /// <summary>Enter on the focused card: chooses the focused option.</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>"Write the rule and allow": answers always with the previewed rules, narrowed as edited.</summary>
    public RelayCommand ConfirmRuleCommand { get; }

    public RelayCommand SendDenyReasonCommand { get; }

    /// <summary>Closes the rule preview or the reason field.</summary>
    public RelayCommand BackCommand { get; }

    public string AutomationName { get => _automationName; private set => SetProperty(ref _automationName, value); }

    /// <summary>Moves the focused option (arrow keys), within the card.</summary>
    public void MoveFocus(int delta)
    {
        if (Options.Count == 0)
            return;
        _focusedIndex = ((_focusedIndex + delta) % Options.Count + Options.Count) % Options.Count;
        UpdateFocus();
    }

    internal override void Apply(TranscriptEntry entry, BlockContext context)
    {
        var card = (ApprovalEntry)entry;
        if (!ReferenceEquals(card.Request, _request))
        {
            _request = card.Request;
            Build(card);
        }

        Title = card.Count > 1 ? $"Approval {card.Position} of {card.Count} · {_request.Tool}" : $"Approval · {_request.Tool}";
        PhoneTitle = card.Count > 1 ? $"Approval {card.Position} of {card.Count}" : "Approval";
        if (card.Next is { } next)
        {
            var action = next.Tool == "shell" ? $"shell {next.Text}" : next.Text;
            var ready = next.ProposedDiff is null ? "" : " (diff ready)";
            NextText = SessionText.Runs(string.Create(CultureInfo.InvariantCulture,
                $"Next in the queue: `{action.Replace('`', '\'')}`{ready} · {card.Position + 1} of {card.Count}"));
            var shortTarget = next.ProposedDiff is { } diff ? $"{next.Tool} {Path.GetFileName(diff.Path)}" : action;
            NextShort = $"Next in the queue: {SafeText.Clean(shortTarget)}{ready}";
        }
        else
        {
            NextText = [];
            NextShort = "";
        }

        Tick(context);
    }

    internal override void Tick(BlockContext context)
    {
        var left = _request.Deadline - context.Now;
        Countdown = SessionText.Countdown(left);
        DeadlineText = $"denied automatically in {Countdown}";
        OnPropertyChanged(nameof(ShortDeadline));
        AutomationName = $"{Title}: {CommandText}. Denied automatically in {SessionText.CountdownWords(left)}.";
    }

    private void Build(TranscriptEntry entry)
    {
        var request = ((ApprovalEntry)entry).Request;
        CommandText = SafeText.Reveal(request.Tool == "shell" || request.Text.StartsWith(request.Tool, StringComparison.Ordinal)
            ? request.Text
            : $"{request.Tool} {request.Text}");
        Unparsed = SafeText.Clean(request.Unparsed);
        Commands = [.. request.Commands.Select(ParsedCommandView.From)];
        Reasons = [.. request.Reasons.Select(reason => new ReasonView(reason.Label, SessionText.Runs(reason.Detail), SessionText.Runs(reason.Short ?? reason.Detail)))];
        ProposedDiff = request.ProposedDiff is { } diff ? new FileDiffView(diff) : null;
        Detections = [.. request.Detections.Select(detection => new DetectionView($"detection: {SafeText.Clean(detection.RuleId)}",
            DetectionText.Detail(detection), SafeText.Clean(detection.Reason), DetectionText.Events(detection)))];
        McpText = request.Mcp is { } mcp ? $"MCP server {SafeText.Clean(mcp.Server)} · trust: {SafeText.Clean(mcp.Trust)}" : "";

        var options = new List<ApprovalOptionView>
        {
            Option(ApprovalOptionKind.Once, "1", "Allow once", "Allow once"),
        };
        if (request.Offered.Contains(ApprovalScope.Session))
            options.Add(Option(ApprovalOptionKind.Session, "2", "This session", "Allow for this session"));
        if (request.Offered.Contains(ApprovalScope.Always))
            options.Add(Option(ApprovalOptionKind.Always, "3", "Always…", "Always, after a rule preview…"));
        options.Add(Option(ApprovalOptionKind.Deny, "4", "Deny", "Deny"));
        options.Add(Option(ApprovalOptionKind.DenyWithReason, "5", "Deny and say why", "Deny and say why"));
        Options = options;
        _focusedIndex = 0;
        RulePreview = request.Offered.Contains(ApprovalScope.Always) && !request.Rules.IsDefaultOrEmpty
            ? new RulePreviewView("Rule to be written · scope: always",
                $"source: approval {request.ApprovalId}, session {_context.Session.SessionId}",
                [.. request.Rules.Select(rule => new RuleRowView(rule))])
            : null;
        if (RulePreview is not null)
        {
            foreach (var row in RulePreview.Rows)
                row.PropertyChanged += (_, _) => ConfirmRuleCommand.NotifyCanExecuteChanged();
        }

        _isRulePreviewOpen = false;
        _isAskingWhy = false;
        OnPropertyChanged(string.Empty);
        UpdateFocus();
    }

    private ApprovalOptionView Option(ApprovalOptionKind kind, string key, string label, string longLabel) =>
        new(kind, key, label, longLabel, new RelayCommand(() => Pick(kind), () => !_isAnswering));

    private void Choose(string key)
    {
        var index = Options.ToList().FindIndex(option => option.Key == key);
        if (index < 0)
            return;
        _focusedIndex = index;
        UpdateFocus();
        Pick(Options[index].OptionKind);
    }

    private void Confirm()
    {
        if (FocusedOption is { } option)
            Pick(option.OptionKind);
    }

    /// <summary>What a button does; buttons work with or without keyboard focus, digits only with it.</summary>
    private void Pick(ApprovalOptionKind kind)
    {
        if (_isAnswering)
            return;
        switch (kind)
        {
            case ApprovalOptionKind.Once:
                Answer(ApprovalAnswer.Once);
                break;
            case ApprovalOptionKind.Session:
                Answer(new ApprovalAnswer(ApprovalChoice.Session));
                break;
            case ApprovalOptionKind.Always when RulePreview is not null:
                IsAskingWhy = false;
                IsRulePreviewOpen = true;
                break;
            case ApprovalOptionKind.Always:
                Answer(new ApprovalAnswer(ApprovalChoice.Always));
                break;
            case ApprovalOptionKind.Deny:
                Answer(ApprovalAnswer.Deny);
                break;
            case ApprovalOptionKind.DenyWithReason:
                IsRulePreviewOpen = false;
                IsAskingWhy = true;
                break;
        }

        UpdateFocus();
    }

    private void ConfirmRule()
    {
        if (RulePreview is not { } preview || preview.Rows.Any(row => row.HasPathsError))
            return;
        Answer(new ApprovalAnswer(ApprovalChoice.Always)
        {
            RulePaths = [.. preview.Rows.Select(row => row.HasPaths ? row.EditedPaths() : ImmutableArray<string>.Empty)],
        });
    }

    private void SendDenyReason()
    {
        var reason = _denyReason.Trim();
        if (reason.Length > 0)
            Answer(new ApprovalAnswer(ApprovalChoice.Deny, reason));
    }

    private void Back()
    {
        IsRulePreviewOpen = false;
        IsAskingWhy = false;
        UpdateFocus();
    }

    private void Answer(ApprovalAnswer answer)
    {
        IsAnswering = true;
        _context.Session.AnswerApproval(this, answer);
    }

    /// <summary>The engine refused or failed the answer; the card can be answered again.</summary>
    internal void AnswerFailed() => IsAnswering = false;

    private void UpdateFocus()
    {
        for (var i = 0; i < Options.Count; i++)
        {
            Options[i].IsFocused = _isFocused && i == _focusedIndex;
            Options[i].IsExpanded = Options[i].OptionKind == ApprovalOptionKind.Always && _isRulePreviewOpen
                || Options[i].OptionKind == ApprovalOptionKind.DenyWithReason && _isAskingWhy;
        }

        OnPropertyChanged(nameof(FocusedOption));
    }

    private void RefreshCommands()
    {
        ChooseCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        ConfirmRuleCommand.NotifyCanExecuteChanged();
        SendDenyReasonCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        foreach (var option in Options)
            option.Choose.NotifyCanExecuteChanged();
    }
}
