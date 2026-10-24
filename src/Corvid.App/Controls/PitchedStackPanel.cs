using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// Stacks its children in rows of one fractional pitch and snaps each row's edges to whole pixels, the way a browser lays
/// out the mockups' rail: rows of 31.575 px come out 32, 31, 32, 31 and stay aligned with the mockup to the pixel. A
/// StackPanel rounds every row up to 32 and drifts seven pixels by the end of the rail. Each child is centred in its row.
/// </summary>
public sealed partial class PitchedStackPanel : Panel
{
    public static readonly DependencyProperty PitchProperty = DependencyProperty.Register(
        nameof(Pitch), typeof(double), typeof(PitchedStackPanel), new PropertyMetadata(32.0, (d, _) => ((PitchedStackPanel)d).InvalidateMeasure()));

    /// <summary>The height of one row in device-independent pixels, fractions allowed (the CSS's padding plus line height).</summary>
    public double Pitch
    {
        get => (double)GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rows = 0;
        var width = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, Pitch));
            if (child.Visibility == Visibility.Collapsed)
                continue;
            rows++;
            width = Math.Max(width, child.DesiredSize.Width);
        }

        return new Size(width, Edge(rows));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var row = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            var top = Edge(row);
            var bottom = Edge(++row);
            child.Arrange(new Rect(0, top, finalSize.Width, bottom - top));
        }

        return finalSize;
    }

    private double Edge(int row) => Math.Round(row * Pitch, MidpointRounding.AwayFromZero);
}
