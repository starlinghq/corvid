using System.ComponentModel;
using Corvid.App.Infrastructure;

namespace Corvid.App.Pages;

/// <summary>
/// The facts table's head row (corvid.css <c>.table th</c>: 12.5 px semibold muted, 6px 10px): "fact", "valid",
/// "recorded", on the columns <see cref="MemoryLayout"/> gives the rows. Built in code so it follows the layout's
/// changes, which a content template made before the first one did not.
/// </summary>
public sealed partial class MemoryFactsHead : Grid
{
    private readonly TextBlock[] _labels;

    public MemoryFactsHead()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
        _labels = [Label("fact", 0), Label("valid", 1), Label("recorded", 2)];
        ApplyColumns();
        Loaded += (_, _) =>
        {
            MemoryLayout.Current.PropertyChanged += OnLayoutChanged;
            ApplyColumns();
            ApplyTheme();
        };
        Unloaded += (_, _) => MemoryLayout.Current.PropertyChanged -= OnLayoutChanged;
        ActualThemeChanged += (_, _) => ApplyTheme();
    }

    private TextBlock Label(string text, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Padding = new Thickness(10, 6, 10, 6),
            TextWrapping = TextWrapping.NoWrap,
        };
        SetColumn(label, column);
        Children.Add(label);
        return label;
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e) => ApplyColumns();

    private void ApplyColumns()
    {
        var layout = MemoryLayout.Current;
        ColumnDefinitions[0].Width = layout.FactColumn;
        ColumnDefinitions[1].Width = layout.ValidColumn;
        ColumnDefinitions[2].Width = layout.RecordedColumn;
    }

    private void ApplyTheme()
    {
        var brush = ThemeBrushes.Get(this, "CorvidText2");
        foreach (var label in _labels)
            label.Foreground = brush;
    }
}
