namespace Corvid.App.Controls;

/// <summary>
/// One segment of a segmented control (Main.dc.html <c>.seg</c>: the approval modes, the theme): pressed is drawn in
/// inverted colours and reported as toggled to screen readers. A click runs the command and never toggles the button
/// itself; <see cref="Microsoft.UI.Xaml.Controls.Primitives.ToggleButton.IsChecked"/> follows the view model through a
/// one-way binding, so the control always shows the state in force (an engine may refuse a mode).
/// </summary>
public sealed partial class SegmentButton : Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
{
    public SegmentButton() => DefaultStyleKey = typeof(SegmentButton);

    protected override void OnToggle()
    {
    }
}
