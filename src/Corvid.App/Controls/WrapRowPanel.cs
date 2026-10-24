using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// Lays its children out left to right and wraps them onto a new line when the width runs out (the dock's tab strip,
/// CSS <c>flex-wrap: wrap</c>). The base controls have no wrapping panel and the app takes no toolkit, hence this one.
/// </summary>
public sealed partial class WrapRowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapRowPanel), new PropertyMetadata(0.0, (d, _) => ((WrapRowPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(
        nameof(LineSpacing), typeof(double), typeof(WrapRowPanel), new PropertyMetadata(0.0, (d, _) => ((WrapRowPanel)d).InvalidateMeasure()));

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

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Place(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Place(double width, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, widest = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                y += lineHeight + LineSpacing;
                x = 0;
                lineHeight = 0;
            }

            if (arrange)
                child.Arrange(new Rect(x, y, size.Width, size.Height));
            widest = Math.Max(widest, x + size.Width);
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return new Size(double.IsInfinity(width) ? widest : Math.Min(widest, width), y + lineHeight);
    }
}
