using System.ComponentModel;

namespace Corvid.App.Pages;

/// <summary>
/// The Memory page's facts table columns, which the page sets from the table's width: the browser's automatic table
/// layout gives the dates more room in a narrow panel (Memory-narrow.png) than in a wide one (Memory.png). The row
/// templates bind to <see cref="Current"/>, so every row and the head row share one set of widths.
/// </summary>
public sealed class MemoryLayout : INotifyPropertyChanged
{
    private bool _narrowTable;

    public static MemoryLayout Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The table is under 400 px wide.</summary>
    public bool NarrowTable
    {
        get => _narrowTable;
        set
        {
            if (_narrowTable == value)
                return;
            _narrowTable = value;
            // By name: x:Bind's static-rooted paths do not refresh on an empty property name.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NarrowTable)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FactColumn)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValidColumn)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordedColumn)));
        }
    }

    public GridLength FactColumn => new(_narrowTable ? 2.2 : 2.3, GridUnitType.Star);

    public GridLength ValidColumn => new(_narrowTable ? 1 : 1.1, GridUnitType.Star);

    public GridLength RecordedColumn => new(_narrowTable ? 1.05 : 0.7, GridUnitType.Star);
}
