using Corvid.App.Core;

namespace Corvid.App.Controls;

/// <summary>
/// A small rounded tag in one of the colour roles (Main.dc.html <c>.tag</c>): "tainted" in amber, "cloud" in violet,
/// "local" in blue. The word carries the meaning; the colour repeats it.
/// </summary>
public sealed partial class TagChip : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TagChip), new PropertyMetadata(""));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(TagChip), new PropertyMetadata(Tone.Grey, (d, _) => ((TagChip)d).UpdateStates(true)));

    public TagChip()
    {
        DefaultStyleKey = typeof(TagChip);
        IsTabStop = false;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Tone Tone
    {
        get => (Tone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates(false);
    }

    private void UpdateStates(bool useTransitions) => VisualStateManager.GoToState(this, Tone.ToString(), useTransitions);
}
