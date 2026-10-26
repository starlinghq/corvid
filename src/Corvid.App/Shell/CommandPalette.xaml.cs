using System.ComponentModel;
using Corvid.App.Core;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Shell;

/// <summary>
/// The palette's view (<see cref="PaletteViewModel"/>). It lives as long as the shell, over everything else. Opening it
/// remembers where focus was and puts the caret in the field; Up and Down (Tab and Shift+Tab too) move through the rows,
/// Page Up and Page Down by eight, Enter chooses, Esc or a click outside closes and gives focus back. A choice that goes
/// somewhere (a page, the composer) keeps the focus it set.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private UIElement? _returnFocus;

    public CommandPalette(PaletteViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        SizeChanged += (_, _) => Fit();
    }

    public PaletteViewModel ViewModel { get; }

    /// <summary>The footer's keys: ↑ ↓ move · ↵ choose · Esc close.</summary>
    public static IReadOnlyList<HintPart> Footer { get; } =
    [
        new("↑", true), new(" ", false), new("↓", true), new(" move   ", false), new("↵", true), new(" choose   ", false),
        new("Esc", true), new(" close", false),
    ];

    public static double RowOpacity(bool enabled) => enabled ? 1 : 0.6;

    public static Visibility IconVisible(IconKind icon) => icon == IconKind.None ? Visibility.Collapsed : Visibility.Visible;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PaletteViewModel.IsOpen) when ViewModel.IsOpen:
                _returnFocus ??= XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as UIElement : null;
                Fit();
                // The field draws in the next layout pass; focus waits for it.
                DispatcherQueue.TryEnqueue(TakeFocus);
                break;
            case nameof(PaletteViewModel.IsOpen):
                // Focus goes back to where it was before the card hides it, or the window would hand it to its first
                // button. A choice that puts it somewhere (the composer, after a prompt from the history) does so after
                // this, through the dispatcher, and wins.
                _returnFocus?.Focus(FocusState.Programmatic);
                _returnFocus = null;
                break;
            case nameof(PaletteViewModel.Mode) when ViewModel.IsOpen:
                DispatcherQueue.TryEnqueue(TakeFocus);
                break;
            case nameof(PaletteViewModel.SelectedIndex):
                DispatcherQueue.TryEnqueue(BringSelectedIntoView);
                break;
            case nameof(PaletteViewModel.SelectionText):
                Announce(ViewModel.SelectionText);
                break;
        }
    }

    /// <summary>
    /// Says the selected row politely: focus stays in the field while the arrows move the selection, so no focus change
    /// announces it.
    /// </summary>
    private void Announce(string text)
    {
        if (SelectionAnnouncer.Text == text)
            return;
        SelectionAnnouncer.Text = text;
        AutomationProperties.SetName(SelectionAnnouncer, text.Length > 0 ? text : "Palette selection");
        if (text.Length > 0)
        {
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(SelectionAnnouncer)
                ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
    }

    private void TakeFocus() => TakeFocus(3);

    /// <summary>Focus into the field (or the card, for a surface without one); retried after layout while the card is still appearing.</summary>
    private void TakeFocus(int tries)
    {
        if (!ViewModel.IsOpen)
            return;
        var taken = ViewModel.HasQuery ? QueryBox.Focus(FocusState.Programmatic) : CardHost.Focus(FocusState.Programmatic);
        if (!taken && tries > 1)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => TakeFocus(tries - 1));
    }

    private void BringSelectedIntoView()
    {
        if (ViewModel.SelectedIndex < 0 || List.ContainerFromIndex(ViewModel.SelectedIndex) is not FrameworkElement row)
            return;
        row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    /// <summary>The card is 640 wide where it fits, the window less 16 px a side where it does not, and never taller than the window.</summary>
    private void Fit()
    {
        if (ActualWidth <= 0)
            return;
        Card.Width = Math.Min(640, Math.Max(0, ActualWidth - 32));
        var top = ActualHeight < 600 ? 16 : 64;
        CardHost.Margin = new Thickness(16, top, 16, 16);
        Card.MaxHeight = Math.Min(560, Math.Max(200, ActualHeight - top - 16));
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsOpen)
            return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Cancel();
                break;
            case VirtualKey.Down:
                ViewModel.MoveSelection(1);
                break;
            case VirtualKey.Up:
                ViewModel.MoveSelection(-1);
                break;
            case VirtualKey.PageDown:
                ViewModel.MoveSelection(8);
                break;
            case VirtualKey.PageUp:
                ViewModel.MoveSelection(-8);
                break;
            // Ctrl+Tab is the switcher's own key and goes to the shell; plain Tab moves through the rows, so focus stays here.
            case VirtualKey.Tab when !ctrl:
                ViewModel.MoveSelection(shift ? -1 : 1);
                break;
            case VirtualKey.Enter:
                // After this key has been handled: a choice that closes the palette hands focus back (to the composer,
                // say), and the Enter still being delivered would type a line break there (seen after Fork…: the
                // session's draft became "\r").
                DispatcherQueue.TryEnqueue(ViewModel.Accept);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Cancel()
    {
        ViewModel.Close();
    }

    /// <summary>
    /// A press on the scrim, not one that bubbled up from the card. A press rather than a tap: the click that opened the
    /// palette (the header's Aside) ends over the scrim that click made appear, and its tap would close it again at once.
    /// </summary>
    private void OnScrimPressed(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(Card).Position;
        if (at.X < 0 || at.Y < 0 || at.X > Card.ActualWidth || at.Y > Card.ActualHeight)
        {
            Cancel();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Cancel();
}
