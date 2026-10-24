using Windows.Foundation;

namespace Corvid.App.Controls;

/// <summary>
/// A gauge (corvid.css <c>.meter</c>): 6 px high, radius 3, bg-3, filled with the accent to <see cref="Fraction"/>, or
/// amber when <see cref="IsWarning"/> (the context near its compaction threshold).
/// </summary>
public sealed partial class Meter : UserControl
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(Meter), new PropertyMetadata(0.0, (d, _) => ((Meter)d).InvalidateArrange()));

    public static readonly DependencyProperty IsWarningProperty = DependencyProperty.Register(
        nameof(IsWarning), typeof(bool), typeof(Meter), new PropertyMetadata(false, (d, _) => ((Meter)d).UpdateFill()));

    private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
    private readonly Grid _track = new() { CornerRadius = new CornerRadius(3) };

    public Meter()
    {
        IsTabStop = false;
        Height = 6;
        _track.Children.Add(_fill);
        Content = _track;
        Loaded += (_, _) => UpdateFill();
        ActualThemeChanged += (_, _) => UpdateFill();
        SizeChanged += (_, _) => InvalidateArrange();
    }

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public bool IsWarning
    {
        get => (bool)GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _fill.Width = Math.Clamp(Fraction, 0, 1) * finalSize.Width;
        return base.ArrangeOverride(finalSize);
    }

    private void UpdateFill()
    {
        _fill.Background = Infrastructure.ThemeBrushes.Get(this, IsWarning ? "CorvidAmber" : "CorvidAccent");
        _track.Background = Infrastructure.ThemeBrushes.Get(this, "CorvidBg3");
    }
}
