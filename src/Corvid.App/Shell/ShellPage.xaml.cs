using System.ComponentModel;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Keys;
using Corvid.App.Core.Shell;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Shell;

/// <summary>
/// The window's content: tells the shell's view model the window's width, shows the current area's page in the content
/// frame, puts the page's dock panels in the column or the overlay, applies the theme, hosts the command palette, and
/// binds the window's keys from the key map (docs/plan/ui.md §6): Ctrl+K and Ctrl+P the palette, Ctrl+Tab the session
/// switcher, Ctrl+R the prompt history, Ctrl+F find in the session, Ctrl+Shift+F search across sessions, Alt+P the
/// generator, F1 and ? the shortcuts, Ctrl+N, Ctrl+Shift+N and Ctrl+; the surfaces of what comes later, Ctrl+B the dock,
/// Ctrl+Shift+B the rail, Ctrl+, Settings, Ctrl+Shift+C the changes in Review; Esc closes an overlay. A remapping in
/// Settings rebinds them at once. A page's own key on the same gesture (Audit's and Memory's Ctrl+F, Models' Alt+P) wins
/// while its area shows: the window leaves that gesture unbound there (<see cref="KeyMap.IsPagesKey"/>).
/// </summary>
public sealed partial class ShellPage : Page
{
    private readonly AppComposition _composition;
    private readonly List<KeyboardAccelerator> _mapped = [];
    private UIElement? _dock;

    public ShellPage(AppComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _composition = composition;
        ViewModel = composition.Shell;
        InitializeComponent();
        ApplyTheme();
        ViewModel.PropertyChanged += OnShellChanged;
        SizeChanged += (_, e) => ViewModel.Layout = ShellLayouts.ForWidth(e.NewSize.Width);
        ContentFrame.Navigated += OnNavigated;
        AddKey(VirtualKey.Escape, VirtualKeyModifiers.None, CloseOverlay);
        // Over everything, so the palette and its surfaces cover the rail, the page, the dock and the overlays.
        Root.Children.Add(new CommandPalette(composition.Palette));
        BindKeys();
        composition.Keys.Changed += (_, _) => BindKeys();
        // Tab moves focus before any accelerator sees it, so gestures on Tab (Ctrl+Tab, the switcher) are caught first.
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        Show(ViewModel.CurrentArea);
        ApplyPhoneHeading();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || e.Handled)
            return;
        var modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control))
            modifiers |= KeyModifiers.Ctrl;
        if (IsDown(VirtualKey.Shift))
            modifiers |= KeyModifiers.Shift;
        if (IsDown(VirtualKey.Menu))
            modifiers |= KeyModifiers.Alt;
        if (modifiers == KeyModifiers.None)
            return;
        var palette = _composition.Palette;
        var switcher = _composition.Keys[KeyCommands.SessionSwitcher].Gesture;
        // The switcher's key with Shift goes back through the list while it is open.
        if (palette is { IsOpen: true, Mode: Core.Palette.PaletteMode.Sessions } && switcher is { } gesture
            && modifiers == (gesture.Modifiers | KeyModifiers.Shift) && gesture.Key == "Tab")
        {
            palette.MoveSelection(-1);
            e.Handled = true;
            return;
        }

        if (_composition.Keys.Find(new KeyGesture(modifiers, "Tab")) is { } binding)
        {
            palette.Execute(binding.Id);
            e.Handled = true;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>One accelerator per window binding in the key map, replaced whole when the map changes.</summary>
    private void BindKeys()
    {
        foreach (var accelerator in _mapped)
            KeyboardAccelerators.Remove(accelerator);
        _mapped.Clear();
        var keys = _composition.Keys;
        foreach (var binding in keys.Bindings.Where(binding => binding.Context == KeyMap.App && binding.Gesture is not null))
        {
            // A gesture the showing page binds too is left out while that page shows: Uno stops at the first accelerator
            // that matches, whether it handles the key or not, and the window's would hide the page's.
            if (keys.IsPagesKey(binding, ViewModel.CurrentArea))
                continue;
            if (!KeyGestures.TryMap(binding.Gesture!, out var key, out var modifiers))
                continue;
            var id = binding.Id;
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                // A bare key ("?") belongs to a text field that has focus: it types there.
                if ((modifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu)) == 0
                    && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox)
                    return;
                _composition.Palette.Execute(id);
                args.Handled = true;
            };
            _mapped.Add(accelerator);
            KeyboardAccelerators.Add(accelerator);
        }
    }

    public ShellViewModel ViewModel { get; }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentArea):
                BindKeys();
                Show(ViewModel.CurrentArea);
                break;
            case nameof(ShellViewModel.Theme):
                ApplyTheme();
                break;
            case nameof(ShellViewModel.ShowDock) or nameof(ShellViewModel.ShowDockOverlay):
                PlaceDock();
                break;
            case nameof(ShellViewModel.PhoneTitleIsHeading):
                ApplyPhoneHeading();
                break;
        }
    }

    /// <summary>The phone top bar's title is the level-1 heading only where the page drops its own (one per page).</summary>
    private void ApplyPhoneHeading() =>
        AutomationProperties.SetHeadingLevel(PhoneTitleText, ViewModel.PhoneTitleIsHeading ? Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1 : Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.None);

    private void Show(Area area)
    {
        var page = AreaPages.For(area);
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, _composition);
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        // Areas are reached from the rail, not by going back, so the frame keeps no history.
        ContentFrame.BackStack.Clear();
        _dock = (e.Content as IDockProvider)?.Dock;
        ViewModel.HasDock = _dock is not null;
        PlaceDock();
    }

    /// <summary>The dock's content lives in one host at a time: the column when it shows, otherwise the overlay.</summary>
    private void PlaceDock()
    {
        DockColumnHost.Content = null;
        DockOverlayHost.Content = null;
        if (_dock is null)
            return;
        if (ViewModel.ShowDock)
            DockColumnHost.Content = _dock;
        else if (ViewModel.ShowDockOverlay)
            DockOverlayHost.Content = _dock;
    }

    private void ApplyTheme() => RequestedTheme = ViewModel.Theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => ViewModel.CloseOverlaysCommand.Execute(null);

    private void CloseOverlay(object? parameter)
    {
        // The palette first: it sits over every other overlay.
        if (_composition.Palette.IsOpen)
            _composition.Palette.Close();
        else if (ViewModel.ShowScrim)
            ViewModel.CloseOverlaysCommand.Execute(null);
    }

    private void AddKey(VirtualKey key, VirtualKeyModifiers modifiers, Action<object?> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Esc belongs to whatever has focus unless an overlay is open to close.
            if (key == VirtualKey.Escape && !ViewModel.ShowScrim && !_composition.Palette.IsOpen)
                return;
            action(null);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }
}
