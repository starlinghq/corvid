using Windows.Foundation;

namespace Corvid.App.Shell;

/// <summary>
/// The status bar's row of items: left to right with a gap, like a horizontal <c>StackPanel</c>, but when they do not
/// all fit whole, items give way by priority rather than by position, and those left keep their order. The mockup's bar
/// clips at the right (<c>overflow: hidden</c>); cut mid-word ("next: nig") the last item read as something else, and
/// cut by position the "tainted" tag went first whenever a page added its own item. So an item that would not fit is
/// left out whole, the highest <see cref="GiveWayProperty"/> first (the area's item and the mode segments before the
/// session's facts, those before the scheduler and the branch, and the tags last); among equals the rightmost goes first.
/// A negative <c>GiveWay</c> never gives way: the taint tag (ui.md §7, taint always shows).
/// <para>
/// A child marked <see cref="IsTrailingProperty"/> (the turn's time) sits at the right edge, in the same contest for
/// room, so it gives way in its turn instead of pushing the row. A child that gave way takes its controls out of the tab
/// order until it shows again (the mode segments), so Tab never lands on something not drawn.
/// </para>
/// </summary>
public sealed partial class FitRowPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(FitRowPanel), new PropertyMetadata(0.0, (d, _) => ((FitRowPanel)d).InvalidateMeasure()));

    /// <summary>How readily a child gives way when the row is short: higher goes first; 0 (the default) goes last; below 0, never.</summary>
    public static readonly DependencyProperty GiveWayProperty = DependencyProperty.RegisterAttached(
        "GiveWay", typeof(int), typeof(FitRowPanel), new PropertyMetadata(0, OnChildLayoutChanged));

    /// <summary>The child sits at the row's right edge rather than after the others.</summary>
    public static readonly DependencyProperty IsTrailingProperty = DependencyProperty.RegisterAttached(
        "IsTrailing", typeof(bool), typeof(FitRowPanel), new PropertyMetadata(false, OnChildLayoutChanged));

    // The controls of children that gave way, with whether each was a tab stop before.
    private readonly Dictionary<Control, bool> _parked = [];

    /// <summary>The gap between items.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static int GetGiveWay(DependencyObject element) => (int)element.GetValue(GiveWayProperty);

    public static void SetGiveWay(DependencyObject element, int value) => element.SetValue(GiveWayProperty, value);

    public static bool GetIsTrailing(DependencyObject element) => (bool)element.GetValue(IsTrailingProperty);

    public static void SetIsTrailing(DependencyObject element, bool value) => element.SetValue(IsTrailingProperty, value);

    private static void OnChildLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
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
        // With an item at the right edge the row takes the whole width it is given.
        var width = !double.IsInfinity(availableSize.Width) && kept.Any(i => GetIsTrailing(Children[i])) ? availableSize.Width : RowWidth(kept);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var kept = Kept(finalSize.Width);
        var trailing = kept.Where(i => GetIsTrailing(Children[i])).ToList();
        var trailingWidth = trailing.Sum(i => Children[i].DesiredSize.Width) + Math.Max(0, trailing.Count - 1) * Spacing;
        double x = 0;
        var right = finalSize.Width - trailingWidth;
        var any = false;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (!kept.Contains(i))
            {
                // Given way, or nothing to show: no space, so nothing of it shows, and Tab passes it by.
                child.Arrange(new Rect(0, 0, 0, 0));
                SetReachable(child, !Shown(child));
                continue;
            }

            SetReachable(child, true);
            var size = child.DesiredSize;
            var top = (finalSize.Height - size.Height) / 2;
            if (GetIsTrailing(child))
            {
                child.Arrange(new Rect(right, top, size.Width, size.Height));
                right += size.Width + Spacing;
                continue;
            }

            var left = x + (any ? Spacing : 0);
            child.Arrange(new Rect(left, top, size.Width, size.Height));
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
        while (RowWidth(kept) > available + 0.5)
        {
            // The most willing to give way goes; among equals, the rightmost. One that never gives way stays, clipped.
            var willing = kept.Where(i => GetGiveWay(Children[i]) >= 0).ToList();
            if (willing.Count == 0)
                break;
            kept.Remove(willing.OrderByDescending(i => GetGiveWay(Children[i])).ThenByDescending(i => i).First());
        }

        return kept;
    }

    /// <summary>The kept children's widths with a gap between each two; the trailing ones count as part of the row.</summary>
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

    /// <summary>Takes a child's controls out of the tab order while it gave way, and puts each back as it was after.</summary>
    private void SetReachable(UIElement child, bool reachable)
    {
        if (!reachable)
        {
            foreach (var control in Controls(child))
            {
                if (_parked.TryAdd(control, control.IsTabStop))
                    control.IsTabStop = false;
            }

            return;
        }

        if (_parked.Count == 0)
            return;
        foreach (var control in Controls(child))
        {
            if (_parked.Remove(control, out var was))
                control.IsTabStop = was;
        }
    }

    private static IEnumerable<Control> Controls(DependencyObject root)
    {
        if (root is Control control)
            yield return control;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var inner in Controls(VisualTreeHelper.GetChild(root, i)))
                yield return inner;
        }
    }
}
