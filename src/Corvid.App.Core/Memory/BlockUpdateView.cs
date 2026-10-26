using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>
/// A staged core block update's card (Memory.png's second panel): the line around the change and the line it adds, the
/// block's size after it against the budget, and Approve, Correct…, Reject and "Consolidate the block…". Correct… edits
/// the added line; consolidation (offered once the block would pass its threshold) proposes the whole block rewritten
/// shorter, which can be edited before it is approved. The budget recounts as the text changes.
/// <para>
/// The added line, its context and a consolidation are untrusted text that approving writes into a block every prompt
/// carries: their hidden characters are shown, and text holding any is approved only once the editor has removed them
/// (it opens with them shown as ⟨U+202E⟩ codes).
/// </para>
/// </summary>
public sealed class BlockUpdateView : ObservableObject
{
    private readonly MemoryViewModel _owner;
    private bool _isCorrecting;
    private bool _isConsolidated;
    private bool _isBusy;
    private string _editText;
    private int _tokensAfter;
    private int _baseTokens;
    private int _baseCount;
    private string _baseText;
    private string _proposedText;
    private int _baseVersion;
    private string _countProblem = "";

    internal BlockUpdateView(MemoryViewModel owner, StagedBlockUpdate update, TimeZoneInfo zone)
    {
        _owner = owner;
        Update = update;
        _editText = MemoryText.Editable(update.Added);
        _baseText = _editText;
        _proposedText = update.Added;
        _tokensAfter = update.TokensAfter;
        _baseTokens = update.TokensAfter;
        _baseCount = -1;
        var block = SafeText.Clean(update.Block);
        var by = SafeText.Clean(update.StagedBy);
        Title =
        [
            new("Core block update · "),
            new(block, MemorySpanStyle.Code),
            new($" · staged {MemoryText.Clock(update.StagedAt, zone)} by {by}"),
        ];
        TitleText = $"Core block update · {block} · staged {MemoryText.Clock(update.StagedAt, zone)} by {by}";
        Why = update.Why.Length == 0 ? "" : "why staged: " + SafeText.Clean(update.Why);
        ConsolidationNote = "consolidation is offered at " + MemoryText.Percent(update.ConsolidateAt);
        Context = SafeText.Reveal(update.Context, keepJoiners: true);
        Added = SafeText.Reveal(update.Added, keepJoiners: true);
        DiffLines = update.Replaces ? [.. update.Diff.Select(row => new BlockDiffLineView(row))] : [];
        ApproveCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.ApproveBlockAsync(this, ct)), CanApprove);
        RejectCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.RejectBlockAsync(this, ct)), () => !_isBusy);
        CorrectCommand = new RelayCommand(ToggleCorrect);
        CancelCommand = new RelayCommand(Cancel);
        ConsolidateCommand = new AsyncRelayCommand(ct => RunAsync(() => _owner.ConsolidateAsync(this, ct)), () => CanConsolidate && !_isBusy);
    }

    public StagedBlockUpdate Update { get; }

    public string Id => Update.Id;

    /// <summary>"Core block update · <c>user</c> · staged 14:02 by cli-9f2e".</summary>
    public IReadOnlyList<MemorySpan> Title { get; }

    public string TitleText { get; }

    /// <summary>"why staged: block writes always wait".</summary>
    public string Why { get; }

    /// <summary>The unchanged line shown as context, any hidden character shown as its code.</summary>
    public string Context { get; }

    /// <summary>The line the update adds, as staged, any hidden character shown as its code.</summary>
    public string Added { get; }

    /// <summary>The text Approve would write holds characters a reader cannot see (or the codes that showed them).</summary>
    public bool HasHiddenText => HiddenIn(TextToWrite);

    /// <summary>The warning while the text to write holds hidden characters; empty otherwise.</summary>
    public string HiddenWarning => HasHiddenText
        ? "This text holds hidden characters, shown as ⟨U+…⟩ codes. Correct… it and delete them; Approve stays off until then."
        : "";

    /// <summary>The text in the editor: the added line (Correct…) or the whole block (after consolidation).</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (!SetProperty(ref _editText, value ?? ""))
                return;
            RaiseHidden();
            ApproveCommand.NotifyCanExecuteChanged();
            Recounting = RecountAsync();
        }
    }

    /// <summary>The text Approve writes: the editor's while it is open, the staged line otherwise.</summary>
    public string TextToWrite => IsEditing ? _editText : _proposedText;

    /// <summary>Correct… is open: the added line is editable.</summary>
    public bool IsCorrecting
    {
        get => _isCorrecting;
        private set
        {
            if (SetProperty(ref _isCorrecting, value))
                RaiseModes();
        }
    }

    /// <summary>The consolidation's rewrite is in the editor, replacing the whole block.</summary>
    public bool IsConsolidated
    {
        get => _isConsolidated;
        private set
        {
            if (SetProperty(ref _isConsolidated, value))
                RaiseModes();
        }
    }

    public bool IsEditing => _isCorrecting || _isConsolidated;

    /// <summary>The two-line diff of an update that adds a line (its context, then the line).</summary>
    public bool ShowsDiff => !IsEditing && !Update.Replaces;

    /// <summary>The line diff of an update that rewrites the whole block (a staged consolidation): kept, removed, added.</summary>
    public bool ShowsRewriteDiff => !IsEditing && Update.Replaces;

    /// <summary>The rewrite's lines against the block now, any hidden character shown as its code.</summary>
    public IReadOnlyList<BlockDiffLineView> DiffLines { get; }

    /// <summary>Approving writes the whole block: a consolidation shown here, or a staged one.</summary>
    public bool ReplacesBlock => _isConsolidated || Update.Replaces;

    public string CorrectLabel => IsEditing ? "Undo changes" : "Correct…";

    /// <summary>What the editor holds, for its label and screen readers.</summary>
    public string EditLabel => _isConsolidated || Update.Replaces
        ? $"The {SafeText.Clean(Update.Block)} block, consolidated: approving replaces the whole block"
        : "The added line, corrected";

    public int TokensAfter
    {
        get => _tokensAfter;
        private set
        {
            if (!SetProperty(ref _tokensAfter, value))
                return;
            OnPropertyChanged(nameof(BudgetText));
            OnPropertyChanged(nameof(BudgetFraction));
            OnPropertyChanged(nameof(IsNearBudget));
            OnPropertyChanged(nameof(CanConsolidate));
            ConsolidateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"332 of 350 tokens".</summary>
    public string BudgetText => string.Create(CultureInfo.InvariantCulture, $"{_tokensAfter} of {Update.Budget} tokens");

    public double BudgetFraction => Update.Budget <= 0 ? 0 : (double)_tokensAfter / Update.Budget;

    /// <summary>At or past the consolidation threshold: the meter turns amber.</summary>
    public bool IsNearBudget => BudgetFraction >= Update.ConsolidateAt;

    /// <summary>"consolidation is offered at 90%".</summary>
    public string ConsolidationNote { get; }

    public bool CanConsolidate => IsNearBudget && !_isConsolidated && !Update.Replaces;

    /// <summary>Why the budget could not be recounted for the edit ("could not count: …"); empty when it was.</summary>
    public string CountProblem
    {
        get => _countProblem;
        private set
        {
            if (SetProperty(ref _countProblem, value))
                OnPropertyChanged(nameof(HasCountProblem));
        }
    }

    public bool HasCountProblem => _countProblem.Length > 0;

    /// <summary>The recount the last edit started; tests await it.</summary>
    internal Task Recounting { get; private set; } = Task.CompletedTask;

    /// <summary>An action of this card is running; the card's other actions wait for it.</summary>
    public bool IsBusy => _isBusy;

    public AsyncRelayCommand ApproveCommand { get; }

    public AsyncRelayCommand RejectCommand { get; }

    public RelayCommand CorrectCommand { get; }

    /// <summary>Esc: back to the staged text.</summary>
    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand ConsolidateCommand { get; }

    internal void ShowConsolidation(BlockConsolidation consolidation)
    {
        var text = MemoryText.Editable(consolidation.Text);
        SetBase(text, consolidation.Tokens);
        IsCorrecting = false;
        IsConsolidated = true;
        SetEditSilently(text);
        TokensAfter = consolidation.Tokens;
    }

    private void ToggleCorrect()
    {
        if (IsEditing)
        {
            Cancel();
            return;
        }

        IsCorrecting = true;
    }

    private void Cancel()
    {
        var text = MemoryText.Editable(Update.Added);
        SetBase(text, Update.TokensAfter);
        IsCorrecting = false;
        IsConsolidated = false;
        SetEditSilently(text);
        TokensAfter = Update.TokensAfter;
    }

    /// <summary>The text the recount measures edits against; a new one makes any count in flight for the old one stale.</summary>
    private void SetBase(string text, int tokens)
    {
        _baseText = text;
        _baseTokens = tokens;
        _baseCount = -1;
        _baseVersion++;
        CountProblem = "";
    }

    private void SetEditSilently(string text)
    {
        _editText = text;
        OnPropertyChanged(nameof(EditText));
        RaiseHidden();
        ApproveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The size after the edit: the staged (or consolidated) size, moved by the edit's change in tokens. A count that
    /// comes back for an older base or an older edit is dropped; a failed count is shown, and the size stays as it was.
    /// </summary>
    private async Task RecountAsync()
    {
        var text = _editText;
        var version = _baseVersion;
        try
        {
            if (_baseCount < 0)
            {
                var baseCount = await _owner.CountTokensAsync(_baseText).ConfigureAwait(true);
                if (version != _baseVersion)
                    return;
                _baseCount = baseCount;
            }

            var count = await _owner.CountTokensAsync(text).ConfigureAwait(true);
            if (version != _baseVersion || !ReferenceEquals(text, _editText))
                return;
            TokensAfter = _baseTokens + count - _baseCount;
            CountProblem = "";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (version == _baseVersion)
                CountProblem = "could not count: " + SafeText.Clean(exception.Message);
        }
    }

    private static bool HiddenIn(string text) => MemoryText.HasHidden(text) || MemoryText.HasRevealedMarks(text);

    private bool CanApprove() => !_isBusy && (!IsEditing || _editText.Trim().Length > 0) && !HiddenIn(TextToWrite);

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
        ConsolidateCommand.NotifyCanExecuteChanged();
    }

    private void RaiseHidden()
    {
        OnPropertyChanged(nameof(HasHiddenText));
        OnPropertyChanged(nameof(HiddenWarning));
    }

    private void RaiseModes()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowsDiff));
        OnPropertyChanged(nameof(ShowsRewriteDiff));
        OnPropertyChanged(nameof(ReplacesBlock));
        OnPropertyChanged(nameof(CorrectLabel));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(CanConsolidate));
        RaiseHidden();
        ConsolidateCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One line of a block rewrite's diff on a Review card: its sign, its text with hidden characters shown.</summary>
public sealed class BlockDiffLineView(BlockDiffRow row)
{
    public BlockDiffRow Row { get; } = row;

    public string Sign { get; } = row.Kind switch
    {
        BlockLineKind.Added => "+",
        BlockLineKind.Removed => "−",
        _ => "",
    };

    public string Text { get; } = SafeText.Reveal(row.Text, keepJoiners: true);

    public bool IsAdded => Row.Kind == BlockLineKind.Added;

    public bool IsRemoved => Row.Kind == BlockLineKind.Removed;

    public string AutomationName => Row.Kind switch
    {
        BlockLineKind.Added => "added: ",
        BlockLineKind.Removed => "removed: ",
        _ => "kept: ",
    } + SafeText.Clean(Row.Text);
}
