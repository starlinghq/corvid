using System.ComponentModel;

namespace Corvid.App.Pages;

/// <summary>
/// A row of the facts table (its head row too): a grid of three columns, fact, valid and recorded, whose widths follow
/// <see cref="MemoryLayout"/>, so every row and the head share them as a table's columns do. Set in code because x:Bind
/// on a template's column widths did not follow the layout's later changes here.
/// </summary>
public sealed partial class MemoryTableRow : Grid
{
    public MemoryTableRow()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        Apply();
        Loaded += (_, _) =>
        {
            MemoryLayout.Current.PropertyChanged += OnLayoutChanged;
            Apply();
        };
        Unloaded += (_, _) => MemoryLayout.Current.PropertyChanged -= OnLayoutChanged;
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    private void Apply()
    {
        var layout = MemoryLayout.Current;
        ColumnDefinitions[0].Width = layout.FactColumn;
        ColumnDefinitions[1].Width = layout.ValidColumn;
        ColumnDefinitions[2].Width = layout.RecordedColumn;
    }
}
