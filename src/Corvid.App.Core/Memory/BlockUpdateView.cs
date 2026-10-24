using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Memory;

/// <summary>
/// A staged core block update's card (Memory.png's second panel): the line around the change and the line it adds, the
/// block's size after it against the budget, and Approve, Correct…, Reject and "Consolidate the block…". Correct… edits
/// the added line; consolidation (offered once the block would pass its threshold) proposes the whole block rewritten
/// shorter, which can be edited before it is approved. The budget recounts as the text changes.
/// </summary>
public sealed class BlockUpdateView : ObservableObject
{
    private readonly MemoryViewModel _owner;
    private bool _isCorrecting;
    private bool _isConsolidated;
    private string _editText;
    private int _tokensAfter;
    private int _baseTokens;
    private int _baseCount;
    private string _baseText;

    internal BlockUpdateView(MemoryViewModel owner, StagedBlockUpdate update, TimeZoneInfo zone)
    {
        _owner = owner;
        Update = update;
        _editText = update.Added;
        _baseText = update.Added;
        _tokensAfter = update.TokensAfter;
        _baseTokens = update.TokensAfter;
        _baseCount = -1;
        Title =
        [
            new("Core block update · "),
            new(update.Block, MemorySpanStyle.Code),
            new($" · staged {MemoryText.Clock(update.StagedAt, zone)} by {update.StagedBy}"),
        ];
        TitleText = $"Core block update · {update.Block} · staged {MemoryText.Clock(update.StagedAt, zone)} by {update.StagedBy}";
        Why = update.Why.Length == 0 ? "" : "why staged: " + update.Why;
        ConsolidationNote = "consolidation is offered at " + MemoryText.Percent(update.ConsolidateAt);
        ApproveCommand = new AsyncRelayCommand(ct => _owner.ApproveBlockAsync(this, ct), () => !IsEditing || _editText.Trim().Length > 0);
        RejectCommand = new AsyncRelayCommand(ct => _owner.RejectBlockAsync(this, ct));
        CorrectCommand = new RelayCommand(ToggleCorrect);
        CancelCommand = new RelayCommand(Cancel);
        ConsolidateCommand = new AsyncRelayCommand(ct => _owner.ConsolidateAsync(this, ct), () => CanConsolidate);
    }

    public StagedBlockUpdate Update { get; }

    public string Id => Update.Id;

    /// <summary>"Core block update · <c>user</c> · staged 14:02 by cli-9f2e".</summary>
    public IReadOnlyList<MemorySpan> Title { get; }

    public string TitleText { get; }

    /// <summary>"why staged: block writes always wait".</summary>
    public string Why { get; }

    /// <summary>The unchanged line shown as context.</summary>
    public string Context => Update.Context;

    /// <summary>The line the update adds, as staged.</summary>
    public string Added => Update.Added;

    /// <summary>The text in the editor: the added line (Correct…) or the whole block (after consolidation).</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (!SetProperty(ref _editText, value ?? ""))
                return;
            ApproveCommand.NotifyCanExecuteChanged();
            _ = RecountAsync();
        }
    }

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

    public bool ShowsDiff => !IsEditing;

    public string CorrectLabel => IsEditing ? "Undo changes" : "Correct…";

    /// <summary>What the editor holds, for its label and screen readers.</summary>
    public string EditLabel => _isConsolidated
        ? $"The {Update.Block} block, consolidated: approving replaces the whole block"
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

    public bool CanConsolidate => IsNearBudget && !_isConsolidated;

    public AsyncRelayCommand ApproveCommand { get; }

    public AsyncRelayCommand RejectCommand { get; }

    public RelayCommand CorrectCommand { get; }

    /// <summary>Esc: back to the staged text.</summary>
    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand ConsolidateCommand { get; }

    internal void ShowConsolidation(BlockConsolidation consolidation)
    {
        _baseText = consolidation.Text;
        _baseTokens = consolidation.Tokens;
        _baseCount = -1;
        IsCorrecting = false;
        IsConsolidated = true;
        SetEditSilently(consolidation.Text);
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
        _baseText = Update.Added;
        _baseTokens = Update.TokensAfter;
        _baseCount = -1;
        IsCorrecting = false;
        IsConsolidated = false;
        SetEditSilently(Update.Added);
        TokensAfter = Update.TokensAfter;
    }

    private void SetEditSilently(string text)
    {
        _editText = text;
        OnPropertyChanged(nameof(EditText));
        ApproveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The size after the edit: the staged (or consolidated) size, moved by the edit's change in tokens.</summary>
    private async Task RecountAsync()
    {
        var text = _editText;
        try
        {
            if (_baseCount < 0)
                _baseCount = await _owner.CountTokensAsync(_baseText).ConfigureAwait(true);
            var count = await _owner.CountTokensAsync(text).ConfigureAwait(true);
            if (ReferenceEquals(text, _editText))
                TokensAfter = _baseTokens + count - _baseCount;
        }
        catch (OperationCanceledException)
        {
            // The page went away; the count no longer matters.
        }
    }

    private void RaiseModes()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(ShowsDiff));
        OnPropertyChanged(nameof(CorrectLabel));
        OnPropertyChanged(nameof(EditLabel));
        OnPropertyChanged(nameof(CanConsolidate));
        ConsolidateCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
    }
}
