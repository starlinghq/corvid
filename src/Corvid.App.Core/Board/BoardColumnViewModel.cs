using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Board;

/// <summary>
/// C-043: one of the Board's kanban columns (docs/plan/ui.md §4.13, Board.dc.html <c>.col</c>): its head with the count
/// ("Needs you · 3"), its cards in the column's order, and, for the columns that grow without end, the first
/// <see cref="Cap"/> cards with "Show N more". A narrow window stacks the columns, each of which can be collapsed.
/// </summary>
public sealed class BoardColumnViewModel : ObservableObject
{
    /// <summary>The most Done cards shown before "Show N more".</summary>
    public const int DoneCap = 20;

    /// <summary>The most Parked cards shown before "Show N more".</summary>
    public const int ParkedCap = 30;

    private int _count;
    private int _hidden;
    private bool _showingAll;
    private bool _isCollapsed;
    private IReadOnlyList<SessionCardViewModel> _all = [];

    public BoardColumnViewModel(BoardColumn column)
    {
        Column = column;
        Cap = column switch
        {
            BoardColumn.Done => DoneCap,
            BoardColumn.Parked => ParkedCap,
            _ => int.MaxValue,
        };
        ShowMoreCommand = new RelayCommand(() =>
        {
            _showingAll = true;
            Apply();
        }, () => _hidden > 0);
        ToggleCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
    }

    public BoardColumn Column { get; }

    public string Name => BoardText.ColumnName(Column);

    /// <summary>"Needs you · 3".</summary>
    public string Head => BoardText.ColumnHead(Column, _count);

    /// <summary>Every card in the column, the hidden ones too.</summary>
    public int Count => _count;

    /// <summary>The cards shown, in the column's order.</summary>
    public ObservableCollection<SessionCardViewModel> Cards { get; } = [];

    /// <summary>The most cards shown until "Show N more" is pressed.</summary>
    public int Cap { get; }

    /// <summary>"Show 12 more"; empty when every card shows.</summary>
    public string MoreText => _hidden > 0 ? $"Show {_hidden} more" : "";

    public bool HasMore => _hidden > 0;

    public bool IsEmpty => _count == 0;

    /// <summary>What an empty column says.</summary>
    public string EmptyText => BoardText.ColumnEmpty(Column);

    /// <summary>Folded to its head (the narrow layout's stacked columns).</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (SetProperty(ref _isCollapsed, value))
                OnPropertyChanged(nameof(ToggleLabel));
        }
    }

    public string ToggleLabel => _isCollapsed ? $"Show {Name}" : $"Hide {Name}";

    public RelayCommand ShowMoreCommand { get; }

    public RelayCommand ToggleCommand { get; }

    /// <summary>The column's cards, in order: the first <see cref="Cap"/> show until "Show N more".</summary>
    internal void Set(IReadOnlyList<SessionCardViewModel> cards)
    {
        _all = cards;
        Apply();
    }

    private void Apply()
    {
        var shown = _showingAll || _all.Count <= Cap ? _all : [.. _all.Take(Cap)];
        BoardViewModel.Sync(Cards, shown);
        var (count, hidden) = (_all.Count, _all.Count - shown.Count);
        if (count != _count)
        {
            _count = count;
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(Head));
            OnPropertyChanged(nameof(IsEmpty));
        }

        if (hidden != _hidden)
        {
            _hidden = hidden;
            OnPropertyChanged(nameof(MoreText));
            OnPropertyChanged(nameof(HasMore));
            ShowMoreCommand.NotifyCanExecuteChanged();
        }
    }
}
