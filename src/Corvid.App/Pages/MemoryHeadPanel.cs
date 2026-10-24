using Windows.Foundation;

namespace Corvid.App.Pages;

/// <summary>
/// A panel head's two parts (corvid.css <c>.panel-head</c>: <c>justify-content: space-between</c>): the title on the
/// left and the detail on the right, each on one line when both fit. When they do not, each gets a share of the width in
/// proportion to its natural width and wraps inside it, as flex items shrink, so a narrow card's head wraps on both sides
/// (Memory-narrow.png) rather than pushing the detail out.
/// </summary>
public sealed partial class MemoryHeadPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(MemoryHeadPanel), new PropertyMetadata(10.0, (d, _) => ((MemoryHeadPanel)d).InvalidateMeasure()));

    private double _leftWidth;
    private double _rightWidth;

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).Take(2).ToList();
        if (visible.Count == 0)
            return default;
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        foreach (var child in visible)
            child.Measure(infinite);
        if (visible.Count == 1)
        {
            visible[0].Measure(availableSize);
            _leftWidth = Math.Min(visible[0].DesiredSize.Width, availableSize.Width);
            _rightWidth = 0;
            return visible[0].DesiredSize;
        }

        var left = visible[0].DesiredSize.Width;
        var right = visible[1].DesiredSize.Width;
        var room = Math.Max(0, availableSize.Width - Spacing);
        if (double.IsInfinity(availableSize.Width) || left + right <= room)
        {
            _leftWidth = left;
            _rightWidth = right;
        }
        else
        {
            var excess = left + right - room;
            _leftWidth = Math.Max(0, left - excess * left / (left + right));
            _rightWidth = Math.Max(0, room - _leftWidth);
        }

        visible[0].Measure(new Size(_leftWidth, availableSize.Height));
        visible[1].Measure(new Size(_rightWidth, availableSize.Height));
        var height = Math.Max(visible[0].DesiredSize.Height, visible[1].DesiredSize.Height);
        var width = double.IsInfinity(availableSize.Width) ? left + right + Spacing : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.Visibility == Visibility.Visible).Take(2).ToList();
        if (visible.Count == 0)
            return finalSize;
        // align-items: center, as the CSS head does.
        var first = visible[0];
        first.Arrange(new Rect(0, (finalSize.Height - first.DesiredSize.Height) / 2, _leftWidth, first.DesiredSize.Height));
        if (visible.Count > 1)
        {
            var second = visible[1];
            second.Arrange(new Rect(Math.Max(0, finalSize.Width - _rightWidth), (finalSize.Height - second.DesiredSize.Height) / 2,
                _rightWidth, second.DesiredSize.Height));
        }

        return finalSize;
    }
}
