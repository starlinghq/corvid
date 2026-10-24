using System.ComponentModel;
using System.Globalization;

namespace Corvid.App.Pages;

/// <summary>
/// The Skills page's table widths, which the page sets from the table's width: under 520 px the library drops its
/// "last used" and "uses" columns and the history its "author" and "when" (the detail panel shows them).
/// </summary>
public sealed class SkillsLayout : INotifyPropertyChanged
{
    private bool _narrow;

    public static SkillsLayout Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Narrow
    {
        get => _narrow;
        set
        {
            if (_narrow == value)
                return;
            _narrow = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Narrow)));
        }
    }
}

/// <summary>
/// A row of one of the Skills page's tables (its head row too): a grid whose column widths come from
/// <see cref="Columns"/>, or <see cref="NarrowColumns"/> while <see cref="SkillsLayout.Narrow"/>, so every row and the
/// head share them as a table's columns do. Widths are "*", "2*" or pixels; 0 hides a column. Set in code, because x:Bind
/// on a template's column widths did not follow later changes (src/Corvid.App/README.md).
/// </summary>
public sealed partial class SkillsTableRow : Grid
{
    private string _columns = "*";
    private string? _narrowColumns;

    public SkillsTableRow()
    {
        Loaded += (_, _) =>
        {
            SkillsLayout.Current.PropertyChanged += OnLayoutChanged;
            Apply();
        };
        Unloaded += (_, _) => SkillsLayout.Current.PropertyChanged -= OnLayoutChanged;
    }

    /// <summary>The columns at full width: "*,96,86,52,56".</summary>
    public string Columns
    {
        get => _columns;
        set
        {
            _columns = value ?? "*";
            Apply();
        }
    }

    /// <summary>The columns while the table is narrow; the wide ones when unset.</summary>
    public string? NarrowColumns
    {
        get => _narrowColumns;
        set
        {
            _narrowColumns = value;
            Apply();
        }
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    private void Apply()
    {
        var spec = (SkillsLayout.Current.Narrow ? _narrowColumns : null) ?? _columns;
        var widths = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        while (ColumnDefinitions.Count < widths.Length)
            ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < ColumnDefinitions.Count; i++)
            ColumnDefinitions[i].Width = i < widths.Length ? Parse(widths[i]) : new GridLength(0);
        // A hidden column's cells go too, so their text cannot spill over the next column.
        foreach (var child in Children.OfType<FrameworkElement>())
        {
            var column = GetColumn(child);
            var hidden = column < ColumnDefinitions.Count && ColumnDefinitions[column].Width is { IsAbsolute: true, Value: 0 };
            child.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static GridLength Parse(string width)
    {
        if (width.EndsWith('*'))
        {
            var factor = width.Length == 1 ? 1 : double.Parse(width[..^1], CultureInfo.InvariantCulture);
            return new GridLength(factor, GridUnitType.Star);
        }

        return width == "auto" ? GridLength.Auto : new GridLength(double.Parse(width, CultureInfo.InvariantCulture));
    }
}
