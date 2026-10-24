using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>
/// A staged fact's card in the review queue (Memory.png's first panel): who staged it and why, the statement, its
/// entities and validity, the nearest existing facts with their scores, the operation, and Approve, Correct… and Reject.
/// Correct… opens the statement for editing (G-8); Approve writes what the editor holds. An update or an invalidation
/// closes one of the nearest facts, picked by clicking it (the closest by default).
/// </summary>
public sealed class StagedFactView : ObservableObject
{
    private readonly MemoryViewModel _owner;
    private MemoryOperation _operation;
    private bool _isCorrecting;
    private string _correctedText;
    private NearbyFactView? _target;

    internal StagedFactView(MemoryViewModel owner, StagedFact fact, TimeZoneInfo zone)
    {
        _owner = owner;
        Fact = fact;
        _correctedText = fact.Text;
        Title = $"Fact · staged {MemoryText.Clock(fact.StagedAt, zone)} by {fact.StagedBy}";
        Why = fact.Why.Length == 0 ? "" : "why staged: " + fact.Why;
        Statement = fact.Predicate.Length == 0
            ? MemoryText.Spans(fact.Subject)
            : [new(fact.Subject, MemorySpanStyle.Bold), new(" "), new(fact.Predicate, MemorySpanStyle.Muted), new(" "), new(fact.Object, MemorySpanStyle.Bold)];
        Entities = [.. fact.Entities.Select(e => e.Label)];
        ValidText = "valid from " + MemoryText.Date(fact.ValidFrom);
        Nearest = [.. fact.Nearest.Select(n => new NearbyFactView(this, n))];
        Evidence = fact.Evidence.Length == 0 ? "" : "evidence: " + fact.Evidence;
        Operations = [.. Enum.GetValues<MemoryOperation>().Select(op =>
            new Choice(Label(op), Describe(op), new RelayCommand(() => Operation = op)))];
        ApproveCommand = new AsyncRelayCommand(ct => _owner.ApproveFactAsync(this, ct), CanApprove);
        RejectCommand = new AsyncRelayCommand(ct => _owner.RejectFactAsync(this, ct));
        CorrectCommand = new RelayCommand(() => IsCorrecting = !IsCorrecting);
        CancelCorrectionCommand = new RelayCommand(() => IsCorrecting = false);
        OpenEvidenceCommand = new RelayCommand(_owner.OpenEvidence, () => Evidence.Length > 0);
        _target = Nearest.FirstOrDefault();
        Operation = fact.Proposed;
        UpdateTargets();
    }

    public StagedFact Fact { get; }

    public string Id => Fact.Id;

    /// <summary>"Fact · staged 09:00 by nightly-tidy".</summary>
    public string Title { get; }

    /// <summary>"why staged: memory_op 0.62, under its floor 0.80".</summary>
    public string Why { get; }

    /// <summary>The statement: the subject and object bold, the relation muted.</summary>
    public IReadOnlyList<MemorySpan> Statement { get; }

    /// <summary>"person · Mark", "habit · CI review".</summary>
    public IReadOnlyList<string> Entities { get; }

    public bool HasEntities => Entities.Count > 0;

    public string ValidText { get; }

    public IReadOnlyList<NearbyFactView> Nearest { get; }

    public bool HasNearest => Nearest.Count > 0;

    public string SearchKind => Fact.SearchKind;

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

    /// <summary>"update closes “Mark prefers quiet UI chrome” from today; click another fact to change it".</summary>
    public string TargetHint => (Target, _operation) switch
    {
        (null, MemoryOperation.Update or MemoryOperation.Invalidate) => "There is no existing fact to close; add it instead.",
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
                CorrectedText = Fact.Text;
            OnPropertyChanged(nameof(CorrectLabel));
            OnPropertyChanged(nameof(IsReading));
        }
    }

    public bool IsReading => !_isCorrecting;

    public string CorrectLabel => _isCorrecting ? "Undo correction" : "Correct…";

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

    private bool CanApprove() =>
        (!_isCorrecting || _correctedText.Trim().Length > 0) && (!NeedsTarget || _target is not null);

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
