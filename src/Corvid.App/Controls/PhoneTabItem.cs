using Corvid.App.Core;

namespace Corvid.App.Controls;

/// <summary>
/// A tab of the phone layout's bottom bar (Phone.dc.html <c>.ph-bar</c>): icon over label, at least 44 px tall for a
/// finger, the current tab in the accent colour, and an amber count beside the icon.
/// </summary>
public sealed partial class PhoneTabItem : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(IconKind), typeof(PhoneTabItem), new PropertyMetadata(IconKind.None));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(PhoneTabItem), new PropertyMetadata(""));

    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(string), typeof(PhoneTabItem), new PropertyMetadata("", (d, _) => ((PhoneTabItem)d).UpdateStates(true)));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(PhoneTabItem), new PropertyMetadata(false, (d, e) => ((PhoneTabItem)d).OnSelectedChanged((bool)e.NewValue)));

    public PhoneTabItem() => DefaultStyleKey = typeof(PhoneTabItem);

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

    public string Count
    {
        get => (string)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
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
        VisualStateManager.GoToState(this, string.IsNullOrEmpty(Count) ? "NoCount" : "HasCount", useTransitions);
    }
}
