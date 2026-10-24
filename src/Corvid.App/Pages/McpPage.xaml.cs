using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mcp;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The MCP area's page. Its view model comes from the composition the shell navigates with. The page arranges itself for
/// the width (<see cref="OpsLayout"/>), moves between servers with Up and Down, closes a question or the form with Esc,
/// focuses the answer a question offers, and scrolls a held card into view when the tools table's Review asks for it.
/// </summary>
public sealed partial class McpPage : Page
{
    public McpPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        Loaded += (_, _) => Arrange();
        // The window's first focus would land on the first button with a keyboard ring: the page takes it instead, and
        // hears Esc from anywhere in it.
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        KeyDown += OnRootKeyDown;
    }

    public McpViewModel ViewModel { get; private set; } = null!;

    /// <summary>"notes__search: the pinned definition beside the new one", the diff's name for a screen reader.</summary>
    public static string DiffName(string tool) => $"{tool}: the pinned definition beside the new one";

    /// <summary>"hash 3e9a41c0 → 7b20d5f9".</summary>
    public static string HashLine(string hashes) => "hash " + hashes;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Detach();
        ViewModel = ((AppComposition)e.Parameter).Mcp;
        Bindings.Update();
        // After the bindings, so a control the change shows is visible when the handler looks for it.
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.ReviewRequested += OnReviewRequested;
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Detach();
        base.OnNavigatedFrom(e);
    }

    private void Detach()
    {
        if (ViewModel is null)
            return;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.ReviewRequested -= OnReviewRequested;
    }

    private void Arrange() =>
        OpsLayout.Arrange(ActualWidth, HeadHost, HeadTitle, HeadButtons, Body, Columns, RightColumn, RightStack);

    private void OnTableSized(object sender, SizeChangedEventArgs e) => OpsLayout.FitTable(sender, e);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(McpViewModel.IsAdding) when ViewModel.IsAdding:
                BodyScroll.ChangeView(null, 0, null, disableAnimation: true);
                FocusSoon(DraftNameBox, FocusState.Programmatic);
                break;
            case nameof(McpViewModel.Confirm) when ViewModel.IsConfirmingTrust:
                FocusSoon(ConfirmTrustButton, FocusState.Keyboard);
                break;
            case nameof(McpViewModel.Confirm) when ViewModel.IsConfirmingForget:
                FocusSoon(ConfirmForgetButton, FocusState.Keyboard);
                break;
        }
    }

    /// <summary>Focuses a control a change just showed, once it is laid out (Uno refuses focus to one not yet measured).</summary>
    private static void FocusSoon(Control control, FocusState state) => OpsLayout.FocusWhenShown(control, state);

    private void OnReviewRequested(object? sender, McpHeldViewModel card) => DispatcherQueue.TryEnqueue(() =>
    {
        if (HeldCards.ContainerFromItem(card) is UIElement container)
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
    });

    /// <summary>Up and Down from a server row: the previous or next server, focused and in view.</summary>
    private void OnRowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var delta = e.Key switch
        {
            VirtualKey.Down => 1,
            VirtualKey.Up => -1,
            _ => 0,
        };
        if (delta == 0)
            return;
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Button { DataContext: McpServerRowViewModel focused } && !ReferenceEquals(focused, ViewModel.Selected))
            focused.SelectCommand.Execute(null);
        ViewModel.MoveSelection(delta);
        if (ViewModel.Selected is { } row && ServerRows.ContainerFromItem(row) is DependencyObject container && Find(container) is { } button)
        {
            button.Focus(FocusState.Keyboard);
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }

        e.Handled = true;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !e.Handled && ViewModel.Close())
            e.Handled = true;
    }

    private static Button? Find(DependencyObject parent)
    {
        if (parent is Button button)
            return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (Find(VisualTreeHelper.GetChild(parent, i)) is { } found)
                return found;
        }

        return null;
    }
}
