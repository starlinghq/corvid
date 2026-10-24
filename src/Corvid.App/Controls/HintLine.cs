using Corvid.App.Core.Sessions;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Controls;

/// <summary>
/// A muted hint with key caps (Main.dc.html <c>.turnstrip</c>'s right-hand side): "Ctrl ↵ steers at the next checkpoint
/// · Esc interrupts", each key drawn as a <c>kbd</c>. The whole line is one name for a screen reader.
/// </summary>
public sealed partial class HintLine : UserControl
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(IReadOnlyList<HintPart>), typeof(HintLine), new PropertyMetadata(null, (d, _) => ((HintLine)d).Rebuild()));

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    public HintLine()
    {
        Content = _row;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public IReadOnlyList<HintPart>? Parts
    {
        get => (IReadOnlyList<HintPart>?)GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    /// <summary>A key cap (<c>kbd</c>): 18 high, 0 5px padding, radius 4, a line border on bg-2, 11 px text-2.</summary>
    public static Border KeyCap(FrameworkElement owner, string key) => new()
    {
        Height = 18,
        Padding = new Thickness(5, 0, 5, 0),
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        BorderBrush = ThemeBrushes.Get(owner, "CorvidLine"),
        Background = ThemeBrushes.Get(owner, "CorvidBg2"),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = key,
            FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"],
            FontSize = 11,
            Foreground = ThemeBrushes.Get(owner, "CorvidText2"),
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private void Rebuild()
    {
        _row.Children.Clear();
        var parts = Parts ?? [];
        foreach (var part in parts)
        {
            _row.Children.Add(part.IsKey
                ? KeyCap(this, part.Text)
                : new TextBlock
                {
                    // A run of spaces keeps its width only with a visible neighbour; a lone space is drawn as a gap.
                    Text = part.Text.Replace(' ', ' '),
                    FontSize = 12.5,
                    Foreground = ThemeBrushes.Get(this, "CorvidText2"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                });
        }

        AutomationProperties.SetName(this, string.Concat(parts.Select(part => part.Text)));
    }
}
