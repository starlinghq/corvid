using Corvid.App.Core;

namespace Corvid.App.Controls;

/// <summary>
/// A destination row in the rail (Main.dc.html <c>.nav</c>): icon, label and attention badge; a hover tint; the selected
/// row in the accent colours with a medium label and an accent badge. The template's visual states draw it, so the
/// colours follow the theme when it changes.
/// </summary>
public sealed partial class RailNavItem : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconKind), typeof(RailNavItem), new PropertyMetadata(IconKind.None));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RailNavItem), new PropertyMetadata(""));

    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(
        nameof(Badge), typeof(string), typeof(RailNavItem), new PropertyMetadata("", (d, _) => ((RailNavItem)d).UpdateStates(true)));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(RailNavItem), new PropertyMetadata(false, (d, e) => ((RailNavItem)d).OnSelectedChanged((bool)e.NewValue)));

    public RailNavItem() => DefaultStyleKey = typeof(RailNavItem);

    public IconKind Icon
    {
        get => (IconKind)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The count as text; empty hides the badge.</summary>
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
