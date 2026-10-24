using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Corvid.App.Controls;

/// <summary>
/// The automation peer of a button that stands for one of a set, the current one drawn in the accent (the rail's areas
/// and sessions, the dock's tabs, the phone's bottom bar): it says which is current as a selection item and in its item
/// status, so the choice is not told by colour alone (docs/plan/ui.md §4.15, §7). Invoking it still clicks the button.
/// </summary>
internal sealed partial class SelectableButtonAutomationPeer(Button owner, Func<bool> isSelected)
    : ButtonAutomationPeer(owner), ISelectionItemProvider
{
    public bool IsSelected => isSelected();

    public IRawElementProviderSimple? SelectionContainer => null;

    public void Select()
    {
        if (!isSelected())
            ((IInvokeProvider)this).Invoke();
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
    }

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);

    protected override string GetItemStatusCore() => isSelected() ? "current" : base.GetItemStatusCore();

    /// <summary>Tells assistive technology that the item's selection changed (a no-op where no client listens).</summary>
    public static void Raise(Button owner, bool selected)
    {
        if (FrameworkElementAutomationPeer.FromElement(owner) is SelectableButtonAutomationPeer peer)
        {
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !selected, selected);
            peer.RaiseAutomationEvent(selected ? AutomationEvents.SelectionItemPatternOnElementSelected : AutomationEvents.PropertyChanged);
        }
    }
}
