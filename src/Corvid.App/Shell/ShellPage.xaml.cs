using System.ComponentModel;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Shell;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Shell;

/// <summary>
/// The window's content: tells the shell's view model the window's width, shows the current area's page in the content
/// frame, puts the page's dock panels in the column or the overlay, applies the theme, and binds the shell's keys
/// (Ctrl+B the dock, Ctrl+Shift+B the rail, Ctrl+, Settings, Esc closes an overlay).
/// </summary>
public sealed partial class ShellPage : Page
{
    private readonly AppComposition _composition;
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
        AddKey(VirtualKey.B, VirtualKeyModifiers.Control, ViewModel.ToggleDockCommand.Execute);
        AddKey(VirtualKey.B, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ViewModel.ToggleRailCommand.Execute);
        AddKey((VirtualKey)188, VirtualKeyModifiers.Control, ViewModel.OpenSettingsCommand.Execute);
        AddKey(VirtualKey.Escape, VirtualKeyModifiers.None, CloseOverlay);
        Show(ViewModel.CurrentArea);
    }

    public ShellViewModel ViewModel { get; }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentArea):
                Show(ViewModel.CurrentArea);
                break;
            case nameof(ShellViewModel.Theme):
                ApplyTheme();
                break;
            case nameof(ShellViewModel.ShowDock) or nameof(ShellViewModel.ShowDockOverlay):
                PlaceDock();
                break;
        }
    }

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
        if (ViewModel.ShowScrim)
            ViewModel.CloseOverlaysCommand.Execute(null);
    }

    private void AddKey(VirtualKey key, VirtualKeyModifiers modifiers, Action<object?> action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Esc belongs to whatever has focus unless an overlay is open to close.
            if (key == VirtualKey.Escape && !ViewModel.ShowScrim)
                return;
            action(null);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }
}
