using Corvid.App.Core;

namespace Corvid.App.Controls;

/// <summary>
/// A transcript row in a colour role (Main.dc.html <c>.row</c>): the escalation in violet, taint in amber, a detection in
/// red, a staged memory or a note in grey with a line border. Text and icons inside take the tone's colour; the words
/// carry the meaning.
/// </summary>
public sealed partial class ToneRow : ContentControl
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(ToneRow), new PropertyMetadata(Tone.Grey, (d, _) => ((ToneRow)d).UpdateStates(true)));

    public ToneRow()
    {
        DefaultStyleKey = typeof(ToneRow);
        IsTabStop = false;
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
