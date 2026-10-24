using Windows.Foundation;

namespace Corvid.App.Shell;

/// <summary>
/// The status bar's row of items: left to right with a gap, like a horizontal <c>StackPanel</c>, but an item that would
/// not fit whole is not drawn, nor is any after it. The mockup's bar clips at the right (<c>overflow: hidden</c>); cut
/// mid-word ("next: nig") the last item read as something else, so the bar ends after the last item that fits.
/// </summary>
public sealed partial class FitRowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(FitRowPanel), new PropertyMetadata(0.0, (d, _) => ((FitRowPanel)d).InvalidateMeasure()));

    /// <summary>The gap between items.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var any = false;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (child.Visibility != Visibility.Visible || child.DesiredSize.Width <= 0)
                continue;
            width += (any ? Spacing : 0) + child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            any = true;
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var any = false;
        var full = false;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (child.Visibility != Visibility.Visible || size.Width <= 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var left = x + (any ? Spacing : 0);
            full |= left + size.Width > finalSize.Width + 0.5;
            if (full)
            {
                // Out of room: this item and the rest are given no space, so nothing of them shows.
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            child.Arrange(new Rect(left, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x = left + size.Width;
            any = true;
        }

        return finalSize;
    }
}
