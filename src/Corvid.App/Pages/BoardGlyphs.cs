using Corvid.App.Controls;
using Corvid.App.Core;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Shapes;

namespace Corvid.App.Pages;

/// <summary>The theme brush for a colour role: the role's foreground token (grey is the second text colour).</summary>
internal static class ToneBrushes
{
    public static string Key(Tone tone) => tone switch
    {
        Tone.Amber => "CorvidAmber",
        Tone.Red => "CorvidRed",
        Tone.Green => "CorvidGreen",
        Tone.Violet => "CorvidViolet",
        Tone.Blue => "CorvidAccent",
        _ => "CorvidText2",
    };
}

/// <summary>
/// A control's foreground from a theme brush named at run time (<c>local:ThemedForeground.Key="{x:Bind …}"</c>), looked up
/// again when the theme changes: a brush returned by an x:Bind function would keep the colour of the theme it was made in.
/// </summary>
public static class ThemedForeground
{
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(ThemedForeground), new PropertyMetadata("", OnKeyChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(ThemedForeground), new PropertyMetadata(false));

    public static string GetKey(Control element) => (string)element.GetValue(KeyProperty);

    public static void SetKey(Control element, string value) => element.SetValue(KeyProperty, value);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (Control)d;
        Apply(control);
        if ((bool)control.GetValue(HookedProperty))
            return;
        control.SetValue(HookedProperty, true);
        control.ActualThemeChanged += (sender, _) => Apply((Control)sender);
        control.Loaded += (sender, _) => Apply((Control)sender);
    }

    private static void Apply(Control control)
    {
        if (GetKey(control) is { Length: > 0 } key)
            control.Foreground = ThemeBrushes.Get(control, key);
    }
}

/// <summary>
/// The 8 px state dot (corvid.css <c>.dot</c>, <c>.dot.run</c>, <c>.dot.wait</c>, <c>.dot.job</c>) in a colour role. The
/// brush is looked up for the element's theme and again when the theme changes, since a bound brush would not repaint.
/// </summary>
public sealed partial class ToneDot : UserControl
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(ToneDot), new PropertyMetadata(Tone.Grey, (d, _) => ((ToneDot)d).Paint()));

    private readonly Ellipse _dot = new() { Width = 8, Height = 8 };

    public ToneDot()
    {
        Width = 8;
        Height = 8;
        IsTabStop = false;
        Content = _dot;
        ActualThemeChanged += (_, _) => Paint();
        Loaded += (_, _) => Paint();
    }

    public Tone Tone
    {
        get => (Tone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    private void Paint() => _dot.Fill = ThemeBrushes.Get(this, ToneBrushes.Key(Tone));
}

/// <summary>
/// An attention item's icon in its colour role (Board.dc.html <c>.tray-item .ic</c>), or in the text colour when
/// <see cref="Plain"/>; repainted when the theme changes.
/// </summary>
public sealed partial class ToneIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(IconKind), typeof(ToneIcon), new PropertyMetadata(IconKind.None, (d, e) => ((ToneIcon)d)._icon.Kind = (IconKind)e.NewValue));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(ToneIcon), new PropertyMetadata(Tone.Grey, (d, _) => ((ToneIcon)d).Paint()));

    public static readonly DependencyProperty PlainProperty = DependencyProperty.Register(
        nameof(Plain), typeof(bool), typeof(ToneIcon), new PropertyMetadata(false, (d, _) => ((ToneIcon)d).Paint()));

    private readonly Icon _icon = new();

    public ToneIcon()
    {
        Width = 16;
        Height = 16;
        IsTabStop = false;
        Content = _icon;
        ActualThemeChanged += (_, _) => Paint();
        Loaded += (_, _) => Paint();
    }

    public IconKind Kind
    {
        get => (IconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Tone Tone
    {
        get => (Tone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public bool Plain
    {
        get => (bool)GetValue(PlainProperty);
        set => SetValue(PlainProperty, value);
    }

    private void Paint() => _icon.Foreground = ThemeBrushes.Get(this, Plain ? "CorvidText" : ToneBrushes.Key(Tone));
}
