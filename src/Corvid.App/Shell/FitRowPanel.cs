using Windows.Foundation;

namespace Corvid.App.Shell;

/// <summary>
/// The status bar's row of items: left to right with a gap, like a horizontal <c>StackPanel</c>, but when they do not
/// all fit whole, items give way by priority rather than by position, and those left keep their order. The mockup's bar
/// clips at the right (<c>overflow: hidden</c>); cut mid-word ("next: nig") the last item read as something else, and
/// cut by position the "tainted" tag went first whenever a page added its own item. So an item that would not fit is
/// left out whole, the highest <see cref="GiveWayProperty"/> first (the area's item and the mode segments before the
/// session's facts, those before the scheduler and the branch, and the tags last: taint is always shown, ui.md §7);
/// among equals the rightmost goes first.
/// </summary>
public sealed partial class FitRowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(FitRowPanel), new PropertyMetadata(0.0, (d, _) => ((FitRowPanel)d).InvalidateMeasure()));

    /// <summary>How readily a child gives way when the row is short: higher goes first; 0 (the default) goes last.</summary>
    public static readonly DependencyProperty GiveWayProperty = DependencyProperty.RegisterAttached(
        "GiveWay", typeof(int), typeof(FitRowPanel), new PropertyMetadata(0, OnGiveWayChanged));

    /// <summary>The gap between items.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static int GetGiveWay(DependencyObject element) => (int)element.GetValue(GiveWayProperty);

    public static void SetGiveWay(DependencyObject element, int value) => element.SetValue(GiveWayProperty, value);

    private static void OnGiveWayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement { Parent: FitRowPanel panel })
            panel.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (Shown(child))
                height = Math.Max(height, child.DesiredSize.Height);
        }

        var kept = Kept(availableSize.Width);
        return new Size(RowWidth(kept), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kept = Kept(finalSize.Width);
        double x = 0;
        var any = false;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!kept.Contains(i))
            {
                // Given way, or nothing to show: no space, so nothing of it shows.
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            var size = child.DesiredSize;
            var left = x + (any ? Spacing : 0);
            child.Arrange(new Rect(left, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x = left + size.Width;
            any = true;
        }

        return finalSize;
    }

    private static bool Shown(UIElement child) => child.Visibility == Visibility.Visible && child.DesiredSize.Width > 0;

    /// <summary>The indexes of the children drawn in <paramref name="available"/>: those shown, less those that gave way.</summary>
    private HashSet<int> Kept(double available)
    {
        var kept = new HashSet<int>();
        for (var i = 0; i < Children.Count; i++)
        {
            if (Shown(Children[i]))
                kept.Add(i);
        }

        if (double.IsInfinity(available))
            return kept;
        while (kept.Count > 0 && RowWidth(kept) > available + 0.5)
        {
            // The most willing to give way goes; among equals, the rightmost.
            var drop = kept.OrderByDescending(i => GetGiveWay(Children[i])).ThenByDescending(i => i).First();
            kept.Remove(drop);
        }

        return kept;
    }

    private double RowWidth(HashSet<int> kept)
    {
        double width = 0;
        var any = false;
        for (var i = 0; i < Children.Count; i++)
        {
            if (!kept.Contains(i))
                continue;
            width += (any ? Spacing : 0) + Children[i].DesiredSize.Width;
            any = true;
        }

        return width;
    }
}
