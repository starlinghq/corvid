namespace Corvid.App.Controls;

/// <summary>
/// A tab of the dock's tab strip (Main.dc.html <c>.tabs</c>): muted text, the selected tab raised on the page background
/// with a medium label, and an optional count badge.
/// </summary>
public sealed partial class DockTab : Button
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(DockTab), new PropertyMetadata(""));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(DockTab), new PropertyMetadata("", (d, _) => ((DockTab)d).UpdateStates(true)));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(DockTab), new PropertyMetadata(false, (d, e) => ((DockTab)d).OnSelectedChanged((bool)e.NewValue)));

    public DockTab() => DefaultStyleKey = typeof(DockTab);

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Badge
    {
        get => (string)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>Says which item is current to assistive technology, not only in the accent colour.</summary>
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new SelectableButtonAutomationPeer(this, () => IsSelected);

    private void OnSelectedChanged(bool selected)
    {
        UpdateStates(true);
        SelectableButtonAutomationPeer.Raise(this, selected);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates(false);
    }

    private void UpdateStates(bool useTransitions)
    {
        VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions);
        VisualStateManager.GoToState(this, string.IsNullOrEmpty(Badge) ? "NoBadge" : "HasBadge", useTransitions);
    }
}
