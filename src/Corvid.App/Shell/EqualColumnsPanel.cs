using Windows.Foundation;

namespace Corvid.App.Shell;

/// <summary>
/// Lays its children out in equal columns across the full width (the phone bar's <c>grid-template-columns: repeat(4, 1fr)</c>),
/// which a StackPanel cannot do and a Grid inside an ItemsControl cannot be told.
/// </summary>
public sealed partial class EqualColumnsPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count;
        if (count == 0)
            return new Size(0, 0);
        var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / count;
        var height = 0.0;
        var widest = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? widest * count : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var count = Children.Count;
        if (count == 0)
            return finalSize;
        var width = finalSize.Width / count;
        for (var i = 0; i < count; i++)
            Children[i].Arrange(new Rect(i * width, 0, width, finalSize.Height));
        return finalSize;
    }
}
