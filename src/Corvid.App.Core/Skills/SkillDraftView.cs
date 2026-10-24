using Corvid.App.Core.Diffs;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Skills;

/// <summary>One step of a replay or an eval case: what was expected and what the turn called, and whether they agree.</summary>
public sealed record ReplayStepView(int Number, string Expected, string Actual, string Verdict, Tone Tone)
{
    public string NumberText => Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string AutomationName => $"step {Number}: expected {(Expected.Length == 0 ? "nothing" : Expected)}, called {(Actual.Length == 0 ? "nothing" : Actual)}, {Verdict}";

    /// <summary>The expected and the actual tool names, step by step; a step only one side has is "missing" or "extra".</summary>
    public static IReadOnlyList<ReplayStepView> Steps(ReplayResult replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var count = Math.Max(replay.Expected.Count, replay.Actual.Count);
        var steps = new List<ReplayStepView>(count);
        for (var i = 0; i < count; i++)
        {
            var expected = i < replay.Expected.Count ? SafeText.Clean(replay.Expected[i]) : "";
            var actual = i < replay.Actual.Count ? SafeText.Clean(replay.Actual[i]) : "";
            var (verdict, tone) = (expected.Length, actual.Length) switch
            {
                (0, _) => ("extra", Tone.Amber),
                (_, 0) => ("missing", Tone.Red),
                _ when string.Equals(expected, actual, StringComparison.Ordinal) => ("match", Tone.Green),
                _ => ("differs", Tone.Red),
            };
            steps.Add(new ReplayStepView(i + 1, expected, actual, verdict, tone));
        }

        return steps;
    }
}

/// <summary>
/// A drafted skill on the Review tab (flow F12): the head says what and why it was staged, the SKILL.md shows as it would
/// be written (every hidden character as its code), the replay's expected and actual steps with its pass or fail, and
/// Approve, Edit… (approve the edited text) and Reject with a reason…. A draft holding a hidden character cannot be
/// approved until Edit… takes it out. One action at a time: a second click while one runs does nothing.
/// </summary>
public sealed class SkillDraftView : ObservableObject
{
    private readonly SkillsViewModel _owner;
    private bool _isEditing;
    private bool _isRejecting;
    private bool _busy;
    private string _editText = "";
    private string _reason = "";

    internal SkillDraftView(SkillsViewModel owner, SkillDraft draft, TimeZoneInfo zone)
    {
        _owner = owner;
        Draft = draft;
        Name = SafeText.Reveal(draft.Skill);
        // As the update's diff shows it (FileDiff): every hidden character as its code, the joiners left as they are.
        Source = SafeText.Reveal(draft.Content.TrimEnd('\n'), keepJoiners: true);
        var origin = $"drafted {SkillsText.Clock(draft.Created, zone)} from {SafeText.Clean(draft.SessionId)}";
        if (draft.Turn is { } turn)
            origin += $", turn {turn}";
        Origin = origin;
        Kind = draft.Replaces is { } replaces
            ? $"updates {Name} at {SkillsText.Short(replaces)}; written as agent"
            : "written as agent, revision 1";
        Why = "why staged: " + SafeText.Clean(draft.Reason);
        Description = SafeText.Clean(SkillsText.Description(draft.Content));
        Steps = ReplayStepView.Steps(draft.Replay);
        ReplayVerdict = draft.Replay.Passed ? "replay passed" : "replay failed";
        ReplayTone = draft.Replay.Passed ? Tone.Green : Tone.Red;
        ReplayDetail = $"the source turn run again with the draft loaded · turn {SafeText.Clean(draft.Replay.Status)}"
            + (draft.Replay.Detail.Length > 0 ? " · " + SafeText.Clean(draft.Replay.Detail) : "");
        Evidence = draft.Events is { } events
            ? $"evidence: {SafeText.Clean(draft.SessionId)} events {events.From} to {events.Through}"
            : $"evidence: {SafeText.Clean(draft.SessionId)}";
        DiffRows = draft.Previous is { } previous
            ? FileDiff.Compute(draft.Skill + "/SKILL.md", previous, draft.Content).Unified()
            : [];

        ApproveCommand = new AsyncRelayCommand(ct => Run(() => _owner.ApproveDraftAsync(this, ct)), CanApprove);
        EditCommand = new RelayCommand(StartEditing, () => !_busy && !_isEditing);
        CancelEditCommand = new RelayCommand(() => IsEditing = false);
        StartRejectingCommand = new RelayCommand(() => { IsEditing = false; IsRejecting = true; }, () => !_busy);
        ConfirmRejectCommand = new AsyncRelayCommand(ct => Run(() => _owner.RejectDraftAsync(this, _reason.Trim(), ct)),
            () => !_busy && _reason.Trim().Length > 0 && !SafeText.HasHidden(_reason));
        BackCommand = new RelayCommand(() => { IsRejecting = false; IsEditing = false; });
        OpenEvidenceCommand = new RelayCommand(() => _owner.OpenSession(draft.SessionId));
    }

    public SkillDraft Draft { get; }

    public string Id => Draft.Id;

    /// <summary>The skill's name, any hidden character shown: approving writes a skill by this name.</summary>
    public string Name { get; }

    public string Title => "Skill draft · " + Name;

    public string Origin { get; }

    public string Kind { get; }

    public string Why { get; }

    public string Description { get; }

    /// <summary>The SKILL.md exactly as it would be written, every hidden character as its code.</summary>
    public string Source { get; }

    /// <summary>An update's change to the skill it replaces; empty for a new skill.</summary>
    public IReadOnlyList<DiffRow> DiffRows { get; }

    public bool IsUpdate => DiffRows.Count > 0;

    public bool IsNew => !IsUpdate;

    public IReadOnlyList<ReplayStepView> Steps { get; }

    public string ReplayVerdict { get; }

    public Tone ReplayTone { get; }

    public string ReplayDetail { get; }

    public string Evidence { get; }

    /// <summary>The draft as staged holds hidden characters: Approve stays off until an edit takes them out.</summary>
    public bool HasHiddenText => SkillsText.HasHiddenContent(Draft.Content) || SafeText.HasHidden(Draft.Skill);

    public string HiddenWarning => SafeText.HasHidden(Draft.Skill)
        ? "The skill's name holds hidden characters, shown as codes. It cannot be approved; reject it."
        : "This SKILL.md holds hidden characters, shown as codes: a model would read text you do not see. Edit… and delete them before approving.";

    public bool IsReading => !_isEditing;

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (!SetProperty(ref _isEditing, value))
                return;
            OnPropertyChanged(nameof(IsReading));
            OnPropertyChanged(nameof(EditProblem));
            OnPropertyChanged(nameof(HasEditProblem));
            OnPropertyChanged(nameof(ShowsActions));
            Refresh();
        }
    }

    public bool IsRejecting
    {
        get => _isRejecting;
        set
        {
            if (!SetProperty(ref _isRejecting, value))
                return;
            OnPropertyChanged(nameof(ShowsActions));
            Refresh();
        }
    }

    /// <summary>The row of Approve, Edit… and Reject…; hidden while a reason is being written.</summary>
    public bool ShowsActions => !_isRejecting;

    /// <summary>The SKILL.md being edited; opens as shown (codes included), so a hidden character has to be deleted.</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (!SetProperty(ref _editText, value ?? ""))
                return;
            OnPropertyChanged(nameof(EditProblem));
            OnPropertyChanged(nameof(HasEditProblem));
            ApproveCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Why the edit cannot be approved yet, or empty.</summary>
    public string EditProblem
    {
        get
        {
            if (!_isEditing)
                return "";
            // A text box's line breaks (\r\n, or a bare \r) are line breaks, not hidden characters.
            var text = SkillsText.Lines(_editText);
            if (text.Trim().Length == 0)
                return "A SKILL.md cannot be empty.";
            if (SafeText.HasHidden(Draft.Skill))
                return HiddenWarning;
            // Nothing hidden is written, nor a code or a control's picture the editor showed in place of a hidden character
            // (only a draft that hid characters opened with them, so elsewhere "⟨U+" or "␛" is ordinary text).
            if (SkillsText.HasHiddenContent(text) || (HasHiddenText && SkillsText.HasRevealedMarks(text)))
                return "Delete the hidden characters (shown as ⟨U+…⟩ codes or control pictures such as ␛) before approving.";
            // Approving writes the skill this draft names; an edit cannot point it at another skill.
            var name = SkillsText.Name(text);
            if (!string.Equals(name, Draft.Skill, StringComparison.Ordinal))
                return name is null
                    ? $"The frontmatter must keep name: {Name}; this draft writes that skill."
                    : $"The frontmatter's name: is “{SafeText.Reveal(name)}”, but this draft writes {Name}. Put the name back, or reject the draft.";
            return "";
        }
    }

    public bool HasEditProblem => EditProblem.Length > 0;

    public string Reason
    {
        get => _reason;
        set
        {
            if (!SetProperty(ref _reason, value ?? ""))
                return;
            OnPropertyChanged(nameof(ReasonProblem));
            OnPropertyChanged(nameof(HasReasonProblem));
            ConfirmRejectCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A reason holding hidden characters, shown, since it is kept with the draft: Reject stays off until they go.</summary>
    public string ReasonProblem => SafeText.HasHidden(_reason)
        ? $"The reason holds hidden characters: “{SafeText.Reveal(_reason.Trim())}”. Delete them to reject."
        : "";

    public bool HasReasonProblem => ReasonProblem.Length > 0;

    /// <summary>What Approve writes: the edit while editing (with its line breaks as <c>\n</c>), the draft otherwise.</summary>
    internal string? EditedContent
    {
        get
        {
            if (!_isEditing)
                return null;
            var text = SkillsText.Lines(_editText).TrimEnd('\n') + "\n";
            // Opened and left as it was: the draft as staged, written as the agent's.
            return string.Equals(text, SkillsText.Lines(Draft.Content).TrimEnd('\n') + "\n", StringComparison.Ordinal) ? null : text;
        }
    }

    public string ApproveLabel => _isEditing ? "Approve as edited" : "Approve";

    public AsyncRelayCommand ApproveCommand { get; }

    public RelayCommand EditCommand { get; }

    public RelayCommand CancelEditCommand { get; }

    public RelayCommand StartRejectingCommand { get; }

    public AsyncRelayCommand ConfirmRejectCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand OpenEvidenceCommand { get; }

    private bool CanApprove()
    {
        if (_busy || _isRejecting || SafeText.HasHidden(Draft.Skill))
            return false;
        return _isEditing ? !HasEditProblem : !HasHiddenText;
    }

    private void StartEditing()
    {
        IsRejecting = false;
        EditText = Source;
        IsEditing = true;
    }

    private async Task Run(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        Refresh();
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(ApproveLabel));
        ApproveCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        StartRejectingCommand.NotifyCanExecuteChanged();
        ConfirmRejectCommand.NotifyCanExecuteChanged();
    }
}
