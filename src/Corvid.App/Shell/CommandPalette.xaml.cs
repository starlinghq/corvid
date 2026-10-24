using System.ComponentModel;
using Corvid.App.Core;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;
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
    private bool _restoreFocus;

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
                _restoreFocus = false;
                Fit();
                // The field draws in the next layout pass; focus waits for it.
                DispatcherQueue.TryEnqueue(TakeFocus);
                break;
            case nameof(PaletteViewModel.IsOpen):
                if (_restoreFocus && _returnFocus is { } back)
                    back.Focus(FocusState.Programmatic);
                _returnFocus = null;
                _restoreFocus = false;
                break;
            case nameof(PaletteViewModel.Mode) when ViewModel.IsOpen:
                DispatcherQueue.TryEnqueue(TakeFocus);
                break;
            case nameof(PaletteViewModel.SelectedIndex):
                DispatcherQueue.TryEnqueue(BringSelectedIntoView);
                break;
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
                ViewModel.Accept();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Cancel()
    {
        _restoreFocus = true;
        ViewModel.Close();
    }

    /// <summary>A click on the scrim, not one that bubbled up from the card.</summary>
    private void OnScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        var at = e.GetPosition(Card);
        if (at.X < 0 || at.Y < 0 || at.X > Card.ActualWidth || at.Y > Card.ActualHeight)
            Cancel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Cancel();
}
