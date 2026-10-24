using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>
/// A lesson candidate's card (Memory.png's third panel): its scope, the rule and when it applies, the evidence, and
/// Approve, "Approve, superseding…" (pick the approved lesson it replaces) and "Reject with a reason…".
/// </summary>
public sealed class LessonCandidateView : ObservableObject
{
    private readonly MemoryViewModel _owner;
    private bool _isSuperseding;
    private bool _isRejecting;
    private string _reason = "";
    private SupersedeChoice? _superseded;

    internal LessonCandidateView(MemoryViewModel owner, LessonCandidate lesson, IEnumerable<Lesson> approved)
    {
        _owner = owner;
        Lesson = lesson;
        Title = lesson.Project.Length == 0 ? "Lesson candidate" : $"Lesson candidate · project {lesson.Project}";
        Text =
        [
            new(lesson.Rule, MemorySpanStyle.Bold),
            new(" "),
            new($"Applies when {lesson.AppliesWhen}.", MemorySpanStyle.Muted),
        ];
        Evidence = string.Join(" · ", new[]
        {
            lesson.Evidence.Length == 0 ? "" : "evidence: " + lesson.Evidence,
            lesson.Kind.Length == 0 ? "" : "kind: " + lesson.Kind,
        }.Where(part => part.Length > 0));
        Supersedable = [.. approved.Where(l => l.SupersededBy is null && l.Project == lesson.Project).Select(l => new SupersedeChoice(this, l))];
        ApproveCommand = new AsyncRelayCommand(ct => _owner.ApproveLessonAsync(this, null, ct));
        StartSupersedingCommand = new RelayCommand(() => { IsRejecting = false; IsSuperseding = true; }, () => Supersedable.Count > 0);
        ConfirmSupersedeCommand = new AsyncRelayCommand(ct => _owner.ApproveLessonAsync(this, _superseded?.Lesson.Id, ct), () => _superseded is not null);
        StartRejectingCommand = new RelayCommand(() => { IsSuperseding = false; IsRejecting = true; });
        ConfirmRejectCommand = new AsyncRelayCommand(ct => _owner.RejectLessonAsync(this, _reason.Trim(), ct), () => _reason.Trim().Length > 0);
        BackCommand = new RelayCommand(() => { IsSuperseding = false; IsRejecting = false; });
    }

    public LessonCandidate Lesson { get; }

    public string Id => Lesson.Id;

    /// <summary>"Lesson candidate · project corvid".</summary>
    public string Title { get; }

    /// <summary>"quality bar passed · waits for review".</summary>
    public string Status => Lesson.Status;

    /// <summary>The rule in bold, then "Applies when …" muted.</summary>
    public IReadOnlyList<MemorySpan> Text { get; }

    /// <summary>"evidence: cli-9f2e events 1182 and 1190 · kind: recovery".</summary>
    public string Evidence { get; }

    /// <summary>The approved lessons of the same project it could replace.</summary>
    public IReadOnlyList<SupersedeChoice> Supersedable { get; }

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
}

/// <summary>An approved lesson a candidate may supersede, as a choice in the card's list.</summary>
public sealed class SupersedeChoice : ObservableObject
{
    private bool _isSelected;

    internal SupersedeChoice(LessonCandidateView owner, Lesson lesson)
    {
        Lesson = lesson;
        Text = MemoryText.Plain(lesson.Rule);
        Detail = $"approved {MemoryText.Date(lesson.Approved)} · applies when {lesson.AppliesWhen}";
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
