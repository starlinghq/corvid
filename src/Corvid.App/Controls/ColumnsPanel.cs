using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// Lays its children out in rows of up to <see cref="Columns"/> equal columns, each row as tall as its tallest child (CSS
/// <c>grid-template-columns: repeat(2, minmax(0, 1fr))</c>: the approval card's parsed commands). Fewer columns are used
/// when each would be narrower than <see cref="MinColumnWidth"/>, as the mockups' narrow widths drop to one.
/// </summary>
public sealed partial class ColumnsPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ColumnsPanel), new PropertyMetadata(2, (d, _) => ((ColumnsPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ColumnsPanel), new PropertyMetadata(10.0, (d, _) => ((ColumnsPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ColumnsPanel), new PropertyMetadata(0.0, (d, _) => ((ColumnsPanel)d).InvalidateMeasure()));

    private readonly List<double> _rowHeights = [];
    private int _columns = 1;

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The gap between columns and between rows.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The narrowest a column may be before the panel uses one column fewer.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        var columns = Math.Max(1, Columns);
        while (columns > 1 && (width - Spacing * (columns - 1)) / columns < MinColumnWidth)
            columns--;
        _columns = columns;
        var cell = Math.Max(0, (width - Spacing * (columns - 1)) / columns);
        _rowHeights.Clear();
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(cell, double.PositiveInfinity));
            var row = i / columns;
            if (row == _rowHeights.Count)
                _rowHeights.Add(0);
            _rowHeights[row] = Math.Max(_rowHeights[row], Children[i].DesiredSize.Height);
        }

        var height = _rowHeights.Sum() + Spacing * Math.Max(0, _rowHeights.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = _columns;
        var cell = Math.Max(0, (finalSize.Width - Spacing * (columns - 1)) / columns);
        var y = 0.0;
        for (var i = 0; i < Children.Count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            if (column == 0 && row > 0)
                y += _rowHeights[row - 1] + Spacing;
            var height = row < _rowHeights.Count ? _rowHeights[row] : Children[i].DesiredSize.Height;
            Children[i].Arrange(new Rect(column * (cell + Spacing), y, cell, height));
        }

        return finalSize;
    }
}
