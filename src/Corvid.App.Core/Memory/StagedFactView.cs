using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>
/// A staged fact's card in the review queue (Memory.png's first panel): who staged it and why, the statement, its
/// entities and validity, the nearest existing facts with their scores, the operation, and Approve, Correct… and Reject.
/// Correct… opens the statement for editing (G-8); Approve writes what the editor holds. An update or an invalidation
/// closes one of the nearest facts: the closest is picked for it only when its score is at least <see cref="SureScore"/>,
/// otherwise the person clicks the one to close before Approve is offered.
/// <para>
/// The statement and the nearest facts are untrusted text that approving writes into memory, which later prompts carry:
/// their hidden characters are shown (<see cref="MemoryText.Spans"/>), and a statement holding any cannot be approved
/// until Correct… removes them (the editor opens with them shown as ⟨U+202E⟩ codes, never silently dropped).
/// </para>
/// </summary>
public sealed class StagedFactView : ObservableObject
{
    /// <summary>The score from which an update or invalidation preselects the closest fact; below it the person picks.</summary>
    public const double SureScore = 0.75;

    private readonly MemoryViewModel _owner;
    private MemoryOperation _operation;
    private bool _isCorrecting;
    private bool _isBusy;
    private string _correctedText;
    private NearbyFactView? _target;

    internal StagedFactView(MemoryViewModel owner, StagedFact fact, TimeZoneInfo zone)
    {
        _owner = owner;
        Fact = fact;
        _correctedText = MemoryText.Editable(fact.Text);
        Title = $"Fact · staged {MemoryText.Clock(fact.StagedAt, zone)} by {SafeText.Clean(fact.StagedBy)}";
        Why = fact.Why.Length == 0 ? "" : "why staged: " + SafeText.Clean(fact.Why);
        Statement = fact.Predicate.Length == 0
            ? MemoryText.Spans(fact.Subject)
            : [.. MemoryText.Spans(fact.Subject, MemorySpanStyle.Bold), new(" "), .. MemoryText.Spans(fact.Predicate, MemorySpanStyle.Muted),
                new(" "), .. MemoryText.Spans(fact.Object, MemorySpanStyle.Bold)];
        HasHiddenText = MemoryText.HasHidden(fact.Text);
        Entities = [.. fact.Entities.Select(e => SafeText.Clean(e.Label))];
        ValidText = "valid from " + MemoryText.Date(fact.ValidFrom);
        Nearest = [.. fact.Nearest.Select(n => new NearbyFactView(this, n))];
        Evidence = fact.Evidence.Length == 0 ? "" : "evidence: " + SafeText.Clean(fact.Evidence);
        Operations = [.. Enum.GetValues<MemoryOperation>().Select(op =>
            new Choice(Label(op), Describe(op), new RelayCommand(() => Operation = op)))];
        ApproveCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.ApproveFactAsync(this, ct)), CanApprove);
        RejectCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.RejectFactAsync(this, ct)), () => !_isBusy);
        CorrectCommand = new RelayCommand(() => IsCorrecting = !IsCorrecting);
        CancelCorrectionCommand = new RelayCommand(() => IsCorrecting = false);
        OpenEvidenceCommand = new RelayCommand(_owner.OpenEvidence, () => Evidence.Length > 0);
        _target = Closest();
        Operation = fact.Proposed;
        UpdateTargets();
    }

    public StagedFact Fact { get; }

    public string Id => Fact.Id;

    /// <summary>"Fact · staged 09:00 by nightly-tidy".</summary>
    public string Title { get; }

    /// <summary>"why staged: memory_op 0.62, under its floor 0.80".</summary>
    public string Why { get; }

    /// <summary>The statement: the subject and object bold, the relation muted, any hidden character shown as its code.</summary>
    public IReadOnlyList<MemorySpan> Statement { get; }

    /// <summary>The staged statement holds characters a reader cannot see; it is written only once corrected.</summary>
    public bool HasHiddenText { get; }

    /// <summary>The warning over a statement with hidden characters; empty when there are none.</summary>
    public string HiddenWarning => HasHiddenText
        ? "This statement holds hidden characters, shown as ⟨U+…⟩ codes. Correct… it and delete them; Approve stays off until then."
        : "";

    /// <summary>"person · the user", "habit · CI review".</summary>
    public IReadOnlyList<string> Entities { get; }

    public bool HasEntities => Entities.Count > 0;

    public string ValidText { get; }

    /// <summary>The nearest existing facts still open; one that another approval closes leaves the list.</summary>
    public ObservableCollection<NearbyFactView> Nearest { get; }

    public bool HasNearest => Nearest.Count > 0;

    public string SearchKind => SafeText.Clean(Fact.SearchKind);

    public string Evidence { get; }

    public bool HasEvidence => Evidence.Length > 0;

    /// <summary>add, update, invalidate, noop; the one in force is selected.</summary>
    public IReadOnlyList<Choice> Operations { get; }

    public MemoryOperation Operation
    {
        get => _operation;
        set
        {
            if (!SetProperty(ref _operation, value) && Operations.Any(o => o.IsSelected))
                return;
            foreach (var (choice, op) in Operations.Zip(Enum.GetValues<MemoryOperation>()))
                choice.IsSelected = op == value;
            OnPropertyChanged(nameof(OperationLabel));
            OnPropertyChanged(nameof(OperationName));
            UpdateTargets();
        }
    }

    /// <summary>The select's text: "add".</summary>
    public string OperationLabel => Label(_operation);

    public string OperationName => $"Operation: {OperationLabel}. {Describe(_operation)}";

    /// <summary>Whether the operation closes an existing fact, so one of the nearest is its target.</summary>
    public bool NeedsTarget => _operation is MemoryOperation.Update or MemoryOperation.Invalidate;

    /// <summary>The fact an update or invalidation closes.</summary>
    public NearbyFactView? Target => NeedsTarget ? _target : null;

    /// <summary>"update replaces “The user prefers quiet UI chrome”, valid until today", or, with none picked, which to pick.</summary>
    public string TargetHint => (Target, _operation) switch
    {
        (null, MemoryOperation.Update or MemoryOperation.Invalidate) when !HasNearest => "There is no existing fact to close; add it instead.",
        (null, MemoryOperation.Update) => "Click the existing fact this update replaces; none is close enough to pick for you.",
        (null, MemoryOperation.Invalidate) => "Click the existing fact to end; none is close enough to pick for you.",
        ({ } t, MemoryOperation.Update) => $"update replaces “{MemoryText.Plain(t.Fact.Text)}”, valid until today. Click another fact to change it.",
        ({ } t, MemoryOperation.Invalidate) => $"invalidate ends “{MemoryText.Plain(t.Fact.Text)}” today and writes nothing new. Click another fact to change it.",
        (_, MemoryOperation.Noop) => "noop writes nothing and clears the proposal.",
        _ => "",
    };

    public bool HasTargetHint => TargetHint.Length > 0;

    /// <summary>The statement is open for editing; Approve writes the edited text.</summary>
    public bool IsCorrecting
    {
        get => _isCorrecting;
        set
        {
            if (!SetProperty(ref _isCorrecting, value))
                return;
            if (!value)
                CorrectedText = MemoryText.Editable(Fact.Text);
            OnPropertyChanged(nameof(CorrectLabel));
            OnPropertyChanged(nameof(IsReading));
            ApproveCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsReading => !_isCorrecting;

    public string CorrectLabel => _isCorrecting ? "Undo correction" : "Correct…";

    /// <summary>The editor's text: the statement, with any hidden character shown as its code to be deleted.</summary>
    public string CorrectedText
    {
        get => _correctedText;
        set
        {
            if (SetProperty(ref _correctedText, value ?? ""))
                ApproveCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The statement Approve writes.</summary>
    public string TextToWrite => _isCorrecting ? _correctedText.Trim() : Fact.Text;

    /// <summary>Whether the approval changes the decider's text.</summary>
    public bool IsCorrected => _isCorrecting && !string.Equals(_correctedText.Trim(), Fact.Text, StringComparison.Ordinal);

    /// <summary>An action of this card is running; the card's other actions wait for it.</summary>
    public bool IsBusy => _isBusy;

    public AsyncRelayCommand ApproveCommand { get; }

    public AsyncRelayCommand RejectCommand { get; }

    public RelayCommand CorrectCommand { get; }

    public RelayCommand CancelCorrectionCommand { get; }

    public RelayCommand OpenEvidenceCommand { get; }

    public static string Label(MemoryOperation operation) => operation.ToString().ToLowerInvariant();

    private static string Describe(MemoryOperation operation) => operation switch
    {
        MemoryOperation.Add => "Write it as a new fact.",
        MemoryOperation.Update => "Write it in place of an existing fact, which is kept as history.",
        MemoryOperation.Invalidate => "End an existing fact today and write nothing new.",
        _ => "Write nothing.",
    };

    internal void SelectTarget(NearbyFactView fact)
    {
        _target = fact;
        if (!NeedsTarget)
            Operation = MemoryOperation.Update;
        UpdateTargets();
    }

    /// <summary>Another approval closed an existing fact: it is no longer offered, nor this card's target.</summary>
    internal void Forget(string factId)
    {
        var gone = Nearest.Where(n => n.Fact.FactId == factId).ToList();
        if (gone.Count == 0)
            return;
        foreach (var fact in gone)
            Nearest.Remove(fact);
        if (_target is not null && gone.Contains(_target))
            _target = Closest();
        OnPropertyChanged(nameof(HasNearest));
        UpdateTargets();
    }

    /// <summary>The closest fact when it is close enough to pick for the person; otherwise none, and the person picks.</summary>
    private NearbyFactView? Closest() =>
        Nearest.MaxBy(n => n.Fact.Score) is { } best && best.Fact.Score >= SureScore ? best : null;

    private bool CanApprove()
    {
        if (_isBusy || (_isCorrecting && _correctedText.Trim().Length == 0) || (NeedsTarget && _target is null))
            return false;
        // Nothing hidden is written, nor a ⟨U+…⟩ code the editor showed in place of a hidden character.
        var text = TextToWrite;
        return !MemoryText.HasHidden(text) && !(HasHiddenText && MemoryText.HasRevealedMarks(text));
    }

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
        RejectCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTargets()
    {
        foreach (var fact in Nearest)
            fact.IsTarget = NeedsTarget && ReferenceEquals(fact, _target);
        OnPropertyChanged(nameof(NeedsTarget));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(HasTargetHint));
        ApproveCommand?.NotifyCanExecuteChanged();
    }
}

/// <summary>An existing fact near a staged one: its text with its date muted, and its score (Memory.png's two rows).</summary>
public sealed class NearbyFactView : ObservableObject
{
    private bool _isTarget;

    internal NearbyFactView(StagedFactView owner, NearbyFact fact)
    {
        Fact = fact;
        // The fact an update would close: shown, hidden characters and all, as the person picks it.
        Spans = [.. MemoryText.Spans(fact.Text), new MemorySpan(" · valid from " + MemoryText.Date(fact.ValidFrom), MemorySpanStyle.Muted | MemorySpanStyle.Small)];
        ScoreText = MemoryText.Score(fact.Score);
        SelectCommand = new RelayCommand(() => owner.SelectTarget(this));
    }

    public NearbyFact Fact { get; }

    public IReadOnlyList<MemorySpan> Spans { get; }

    public string ScoreText { get; }

    /// <summary>The fact the card's update or invalidation would close.</summary>
    public bool IsTarget
    {
        get => _isTarget;
        set
        {
            if (SetProperty(ref _isTarget, value))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public string AutomationName =>
        $"{MemoryText.Plain(Fact.Text)}, valid from {MemoryText.Date(Fact.ValidFrom)}, score {ScoreText}{(_isTarget ? ", the fact this closes" : "")}";

    public RelayCommand SelectCommand { get; }
}
