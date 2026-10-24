using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// A file path in one mono line that, when it does not fit, gives up its start rather than its end:
/// "…andbox.Tests/SeccompTests.cs" (the mockups' <c>.files .path</c>, which CSS cuts with <c>direction: rtl</c>). It fits
/// the width it is given, so the same path shows whole in a wide column and cut in a narrow one. The full path is the
/// tooltip and the accessible name.
/// </summary>
public sealed partial class PathText : Panel
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(PathText), new PropertyMetadata("", (d, _) => ((PathText)d).OnPathChanged()));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PathText), new PropertyMetadata(null, (d, e) => ((PathText)d)._text.Foreground = e.NewValue as Brush));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(PathText), new PropertyMetadata(12.5, (d, e) => ((PathText)d).OnFontChanged()));

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.NoWrap };

    public PathText()
    {
        _text.FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"];
        _text.FontSize = 12.5;
        Children.Add(_text);
    }

    public string Path
    {
        get => (string)GetValue(PathProperty);
        set => SetValue(PathProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    private void OnPathChanged()
    {
        ToolTipService.SetToolTip(this, string.IsNullOrEmpty(Path) ? null : Path);
        AutomationProperties.SetName(this, Path ?? "");
        InvalidateMeasure();
    }

    private void OnFontChanged()
    {
        _text.FontSize = FontSize;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var path = Path ?? "";
        var width = availableSize.Width;
        if (Fits(path, width) || path.Length < 2)
            return _text.DesiredSize;

        // The longest end of the path that fits after the ellipsis.
        int low = 1, high = path.Length - 1;
        while (low < high)
        {
            var keep = (low + high + 1) / 2;
            if (Fits("…" + path[^keep..], width))
                low = keep;
            else
                high = keep - 1;
        }

        Fits("…" + path[^low..], width);
        return new Size(Math.Min(_text.DesiredSize.Width, width), _text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, _text.DesiredSize.Width), finalSize.Height));
        return finalSize;
    }

    private bool Fits(string text, double width)
    {
        _text.Text = text;
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return double.IsInfinity(width) || _text.DesiredSize.Width <= width + 0.5;
    }
}
