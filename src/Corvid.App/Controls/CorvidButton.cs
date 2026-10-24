namespace Corvid.App.Controls;

/// <summary>How a <see cref="CorvidButton"/> looks (Main.dc.html <c>.btn</c>, <c>.btn.primary</c>, <c>.btn.ghost</c>).</summary>
public enum ButtonVariant
{
    /// <summary>A bordered button on the page background.</summary>
    Default,
    /// <summary>Filled with the accent; text in <c>CorvidOnAccent</c>.</summary>
    Primary,
    /// <summary>No border and no fill until hovered.</summary>
    Ghost,
}

/// <summary>
/// The mockups' text button (<c>.btn</c>): 30 px high, radius 6, weight 500, in one of three variants. The small size
/// (<c>.btn.sm</c>) is the <c>CorvidSmallButton</c> style; a red label (<c>.btn.danger</c>) is a Foreground.
/// </summary>
public sealed partial class CorvidButton : Button
{
    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(ButtonVariant), typeof(CorvidButton), new PropertyMetadata(ButtonVariant.Default, (d, _) => ((CorvidButton)d).UpdateStates(true)));

    public CorvidButton() => DefaultStyleKey = typeof(CorvidButton);

    public ButtonVariant Variant
    {
        get => (ButtonVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates(false);
    }

    private void UpdateStates(bool useTransitions) => VisualStateManager.GoToState(this, Variant.ToString(), useTransitions);
}
