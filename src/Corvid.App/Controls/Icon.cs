using Corvid.App.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Corvid.App.Controls;

/// <summary>
/// One of the mockups' 16 px icons, stroked (1.6, round caps and joins) in the control's <see cref="Control.Foreground"/>,
/// which a template binds or the visual tree passes down. The logo is the one filled glyph. An icon is decorative and
/// hidden from screen readers: the button or row it sits in carries the name (AutomationProperties.Name on an icon-only
/// button). Never an emoji or a font glyph, so every platform draws the same thing.
/// <para>
/// Uno rounds a shape's stroke thickness to whole pixels, which would draw 1.6 as 2 and thicken every icon. So the path is
/// drawn five times larger with an 8 px stroke, a whole number, and scaled back to 16 px: the stroke lands at 1.6 as the
/// mockups' SVGs draw it.
/// </para>
/// </summary>
public sealed partial class Icon : UserControl
{
    private const double Oversample = 5;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(Icon), new PropertyMetadata(IconKind.None, (d, _) => ((Icon)d).Redraw()));

    private readonly ShapePath _path = new()
    {
        StrokeThickness = 1.6 * Oversample,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Stretch = Stretch.None,
        RenderTransform = new ScaleTransform { ScaleX = 1 / Oversample, ScaleY = 1 / Oversample },
    };

    public Icon()
    {
        Width = 16;
        Height = 16;
        IsTabStop = false;
        // A canvas neither clips nor stretches the oversized path; the transform brings it back to 16 by 16.
        Content = new Canvas { Width = 16, Height = 16, Children = { _path } };
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Paint());
    }

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void Redraw()
    {
        var geometry = IconPaths.Create(Kind);
        if (geometry is not null)
            geometry.Transform = new ScaleTransform { ScaleX = Oversample, ScaleY = Oversample };
        _path.Data = geometry;
        Paint();
    }

    private void Paint()
    {
        var filled = IconPaths.IsFilled(Kind);
        _path.Fill = filled ? Foreground : null;
        _path.Stroke = filled ? null : Foreground;
    }
}
