using System.Globalization;
using Corvid.App.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// A file path in one mono line that, when it does not fit, gives up its start rather than its end:
/// "…andbox.Tests/SeccompTests.cs" (the mockups' <c>.files .path</c>, which CSS cuts with <c>direction: rtl</c>). It fits
/// the width it is given, so the same path shows whole in a wide column and cut in a narrow one. The full path is the
/// tooltip and the accessible name.
/// <para>
/// Every path Review and Rewind draw goes through here, and a person acts on each (reverts it, restores it, opens it), so
/// a hidden character in one (a bidirectional override, a zero-width character) is shown as a code
/// (<see cref="SafeText.Reveal"/>) in the text, the tooltip and the accessible name alike. The cut falls between whole
/// characters, never inside a surrogate pair or a revealed code's text.
/// </para>
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
    private string _shown = "";
    private int[] _starts = [];

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
        _shown = SafeText.Reveal(Path);
        // Where each text element (a whole character, a surrogate pair, a base with its marks) starts: the places a cut may fall.
        _starts = StringInfo.ParseCombiningCharacters(_shown);
        ToolTipService.SetToolTip(this, _shown.Length == 0 ? null : _shown);
        AutomationProperties.SetName(this, _shown);
        InvalidateMeasure();
    }

    private void OnFontChanged()
    {
        _text.FontSize = FontSize;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var path = _shown;
        var width = availableSize.Width;
        if (Fits(path, width) || _starts.Length < 2)
            return _text.DesiredSize;

        // The longest end of the path, in whole text elements, that fits after the ellipsis.
        int low = 1, high = _starts.Length - 1;
        while (low < high)
        {
            var keep = (low + high + 1) / 2;
            if (Fits("…" + Tail(keep), width))
                low = keep;
            else
                high = keep - 1;
        }

        Fits("…" + Tail(low), width);
        return new Size(Math.Min(_text.DesiredSize.Width, width), _text.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(new Rect(0, 0, Math.Max(finalSize.Width, _text.DesiredSize.Width), finalSize.Height));
        return finalSize;
    }

    /// <summary>The path's last <paramref name="elements"/> text elements.</summary>
    private string Tail(int elements) => _shown[_starts[_starts.Length - elements]..];

    private bool Fits(string text, double width)
    {
        _text.Text = text;
        _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return double.IsInfinity(width) || _text.DesiredSize.Width <= width + 0.5;
    }
}
