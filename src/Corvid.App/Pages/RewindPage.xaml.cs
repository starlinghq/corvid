using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Shell;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Rewind area's page (Rewind.png, F5). Its view model comes from the composition the shell navigates with. Keys: Up
/// and Down move through the ledger (over calls still running), Enter or a click selects, Esc closes the preview and
/// otherwise goes back to the session (Esc Esc on an empty composer came here). Under 720 px the plan goes below the
/// ledger.
/// </summary>
public sealed partial class RewindPage : Page
{
    private ShellViewModel? _shell;

    public RewindPage()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    public RewindViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var composition = (AppComposition)e.Parameter;
        ViewModel = composition.Rewind;
        _shell = composition.Shell;
        _shell.PropertyChanged += OnShellChanged;
        ViewModel.Refresh();
        Bindings.Update();
        ApplyLayout();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_shell is not null)
            _shell.PropertyChanged -= OnShellChanged;
        base.OnNavigatedFrom(e);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Up or VirtualKey.Down when ViewModel is not null:
                if (ViewModel.MoveSelection(e.Key == VirtualKey.Down ? 1 : -1) is { } entry)
                    FocusRow(entry);
                e.Handled = true;
                break;
            case VirtualKey.Escape when ViewModel is not null:
                if (ViewModel.IsPreviewing)
                    ViewModel.ClosePreviewCommand.Execute(null);
                else
                    ViewModel.BackToSessionCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Keyboard focus follows the selection, so the row's outline shows where the arrows went.</summary>
    private void FocusRow(LedgerEntryView entry)
    {
        var index = ViewModel.Entries.IndexOf(entry);
        if (index >= 0 && Ledger.ContainerFromIndex(index) is ContentPresenter presenter
            && FindButton(presenter) is { } button)
            button.Focus(FocusState.Keyboard);
    }

    private static Button? FindButton(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button)
                return button;
            if (FindButton(child) is { } found)
                return found;
        }

        return null;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Layout))
            ApplyLayout();
    }

    private void ApplyLayout()
    {
        var phone = (_shell?.Layout ?? ShellLayout.Wide) == ShellLayout.Phone;
        RightColumn.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = phone ? 0 : 18;
        Body.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 18, 24, 18);
        Head.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);
        EscHint.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(Right, phone ? 1 : 0);
        Grid.SetColumn(Right, phone ? 0 : 1);
    }
}
