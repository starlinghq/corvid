using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// Lays its children out left to right and wraps them onto a new line when the width runs out (the dock's tab strip,
/// CSS <c>flex-wrap: wrap</c>). The base controls have no wrapping panel and the app takes no toolkit, hence this one.
/// Each child is arranged across its line's height, so <c>VerticalAlignment="Center"</c> centres it on the line as
/// <c>align-items: center</c> does; <see cref="PushLastRight"/> is the last item's <c>margin-left: auto</c>.
/// </summary>
public sealed partial class WrapRowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapRowPanel), new PropertyMetadata(0.0, (d, _) => ((WrapRowPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WrapRowPanel), new PropertyMetadata(0.0, (d, _) => ((WrapRowPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty PushLastRightProperty = DependencyProperty.Register(
        nameof(PushLastRight), typeof(bool), typeof(WrapRowPanel), new PropertyMetadata(false, (d, _) => ((WrapRowPanel)d).InvalidateMeasure()));

    /// <summary>The gap between items on a line.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>The gap between lines.</summary>
    public double LineSpacing
    {
        get => (double)GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }

    /// <summary>The last item sits at the right edge of its line (the turn strip's hint).</summary>
    public bool PushLastRight
    {
        get => (bool)GetValue(PushLastRightProperty);
        set => SetValue(PushLastRightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        var size = Place(availableSize.Width, arrange: false);
        return PushLastRight && !double.IsInfinity(availableSize.Width) ? new Size(availableSize.Width, size.Height) : size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Place(double width, bool arrange)
    {
        var visible = Children.Where(child => child.Visibility != Visibility.Collapsed).ToList();
        var lines = new List<(int Start, int End, double Height)>();
        double x = 0, lineHeight = 0, widest = 0;
        var start = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var size = visible[i].DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                lines.Add((start, i, lineHeight));
                start = i;
                x = 0;
                lineHeight = 0;
            }

            widest = Math.Max(widest, x + size.Width);
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        if (visible.Count > 0)
            lines.Add((start, visible.Count, lineHeight));

        double y = 0;
        foreach (var (first, end, height) in lines)
        {
            if (arrange)
            {
                x = 0;
                for (var i = first; i < end; i++)
                {
                    var size = visible[i].DesiredSize;
                    if (PushLastRight && i == visible.Count - 1 && !double.IsInfinity(width))
                        x = Math.Max(x, width - size.Width);
                    visible[i].Arrange(new Rect(x, y, size.Width, height));
                    x += size.Width + Spacing;
                }
            }

            y += height + LineSpacing;
        }

        var total = lines.Count == 0 ? 0 : y - LineSpacing;
        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), total);
    }
}
