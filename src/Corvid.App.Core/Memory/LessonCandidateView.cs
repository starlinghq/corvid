using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>
/// A lesson candidate's card (Memory.png's third panel): its scope, the rule and when it applies, the evidence, and
/// Approve, "Approve, superseding…" (pick the approved lesson it replaces) and "Reject with a reason…".
/// <para>
/// The rule and "applies when" are untrusted text that approving carries into later prompts: their hidden characters are
/// shown, and a lesson holding any can only be rejected, since a lesson has no editor to correct it in.
/// </para>
/// </summary>
public sealed class LessonCandidateView : ObservableObject
{
    private readonly MemoryViewModel _owner;
    private bool _isSuperseding;
    private bool _isRejecting;
    private bool _isBusy;
    private string _reason = "";
    private SupersedeChoice? _superseded;

    internal LessonCandidateView(MemoryViewModel owner, LessonCandidate lesson, IEnumerable<Lesson> approved)
    {
        _owner = owner;
        Lesson = lesson;
        var project = SafeText.Clean(lesson.Project);
        Title = project.Length == 0 ? "Lesson candidate" : $"Lesson candidate · project {project}";
        Text =
        [
            .. MemoryText.Spans(lesson.Rule, MemorySpanStyle.Bold),
            new(" "),
            new($"Applies when {MemoryText.Plain(lesson.AppliesWhen)}.", MemorySpanStyle.Muted),
        ];
        HasHiddenText = MemoryText.HasHidden(lesson.Rule) || MemoryText.HasHidden(lesson.AppliesWhen);
        Evidence = string.Join(" · ", new[]
        {
            lesson.Evidence.Length == 0 ? "" : "evidence: " + SafeText.Clean(lesson.Evidence),
            lesson.Kind.Length == 0 ? "" : "kind: " + SafeText.Clean(lesson.Kind),
        }.Where(part => part.Length > 0));
        ApproveCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.ApproveLessonAsync(this, null, ct)), CanApprove);
        StartSupersedingCommand = new RelayCommand(() => { IsRejecting = false; IsSuperseding = true; }, () => Supersedable.Count > 0 && !HasHiddenText);
        ConfirmSupersedeCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.ApproveLessonAsync(this, _superseded?.Lesson.Id, ct)),
            () => _superseded is not null && CanApprove());
        StartRejectingCommand = new RelayCommand(() => { IsSuperseding = false; IsRejecting = true; });
        ConfirmRejectCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.RejectLessonAsync(this, _reason.Trim(), ct)),
            () => !_isBusy && _reason.Trim().Length > 0);
        BackCommand = new RelayCommand(() => { IsSuperseding = false; IsRejecting = false; });
        Refresh(approved);
    }

    public LessonCandidate Lesson { get; }

    public string Id => Lesson.Id;

    /// <summary>"Lesson candidate · project corvid".</summary>
    public string Title { get; }

    /// <summary>"quality bar passed · waits for review".</summary>
    public string Status => SafeText.Clean(Lesson.Status);

    /// <summary>The rule in bold, then "Applies when …" muted, any hidden character shown as its code.</summary>
    public IReadOnlyList<MemorySpan> Text { get; }

    /// <summary>The rule or its condition holds characters a reader cannot see: the lesson can only be rejected.</summary>
    public bool HasHiddenText { get; }

    /// <summary>The warning over a lesson with hidden characters; empty when there are none.</summary>
    public string HiddenWarning => HasHiddenText
        ? "This lesson holds hidden characters, shown as ⟨U+…⟩ codes. It cannot be approved as drawn; reject it with a reason."
        : "";

    /// <summary>"evidence: cli-9f2e events 1182 and 1190 · kind: recovery".</summary>
    public string Evidence { get; }

    /// <summary>The approved lessons of the same project it could replace, as they stand after the last approval.</summary>
    public ObservableCollection<SupersedeChoice> Supersedable { get; } = [];

    public bool IsSuperseding
    {
        get => _isSuperseding;
        private set
        {
            if (SetProperty(ref _isSuperseding, value))
                OnPropertyChanged(nameof(ShowsActions));
        }
    }

    public bool IsRejecting
    {
        get => _isRejecting;
        private set
        {
            if (SetProperty(ref _isRejecting, value))
                OnPropertyChanged(nameof(ShowsActions));
        }
    }

    /// <summary>The three buttons show until one of the two follow-ups opens.</summary>
    public bool ShowsActions => !_isSuperseding && !_isRejecting;

    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value ?? ""))
                ConfirmRejectCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>An action of this card is running; the card's other actions wait for it.</summary>
    public bool IsBusy => _isBusy;

    public AsyncRelayCommand ApproveCommand { get; }

    public RelayCommand StartSupersedingCommand { get; }

    public AsyncRelayCommand ConfirmSupersedeCommand { get; }

    public RelayCommand StartRejectingCommand { get; }

    public AsyncRelayCommand ConfirmRejectCommand { get; }

    /// <summary>Esc or Back: closes the follow-up.</summary>
    public RelayCommand BackCommand { get; }

    internal void Choose(SupersedeChoice choice)
    {
        _superseded = choice;
        foreach (var item in Supersedable)
            item.IsSelected = ReferenceEquals(item, choice);
        ConfirmSupersedeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Lists again the approved lessons it could replace: one another approval superseded leaves, one it approved joins.
    /// The choice stays when its lesson is still there.
    /// </summary>
    internal void Refresh(IEnumerable<Lesson> approved)
    {
        var chosen = _superseded?.Lesson.Id;
        Supersedable.Clear();
        _superseded = null;
        foreach (var lesson in approved.Where(l => l.SupersededBy is null && l.Project == Lesson.Project))
        {
            var choice = new SupersedeChoice(this, lesson);
            Supersedable.Add(choice);
            if (lesson.Id == chosen)
                _superseded = choice;
        }

        foreach (var item in Supersedable)
            item.IsSelected = ReferenceEquals(item, _superseded);
        if (Supersedable.Count == 0 && _isSuperseding)
            IsSuperseding = false;
        StartSupersedingCommand.NotifyCanExecuteChanged();
        ConfirmSupersedeCommand.NotifyCanExecuteChanged();
    }

    private bool CanApprove() => !_isBusy && !HasHiddenText;

    private async Task RunAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        if (!SetProperty(ref _isBusy, value, nameof(IsBusy)))
            return;
        ApproveCommand.NotifyCanExecuteChanged();
        ConfirmSupersedeCommand.NotifyCanExecuteChanged();
        ConfirmRejectCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>An approved lesson a candidate may supersede, as a choice in the card's list.</summary>
public sealed class SupersedeChoice : ObservableObject
{
    private bool _isSelected;

    internal SupersedeChoice(LessonCandidateView owner, Lesson lesson)
    {
        Lesson = lesson;
        Text = MemoryText.Plain(lesson.Rule);
        Detail = $"approved {MemoryText.Date(lesson.Approved)} · applies when {MemoryText.Plain(lesson.AppliesWhen)}";
        ChooseCommand = new RelayCommand(() => owner.Choose(this));
    }

    public Lesson Lesson { get; }

    public string Text { get; }

    public string Detail { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string AutomationName => $"Supersede: {Text}";

    public RelayCommand ChooseCommand { get; }
}
