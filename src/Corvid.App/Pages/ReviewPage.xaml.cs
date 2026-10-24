using System.ComponentModel;
using Corvid.App.Controls;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Review;
using Corvid.App.Core.Shell;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Review area's page (Review.png, F4). Its view model comes from the composition the shell navigates with. The
/// diff's columns stack under 860 px, as Review-narrow.png shows, and under 720 px the Files panel goes above the diff.
/// Keys: Up and Down on the file list move through the files.
/// </summary>
public sealed partial class ReviewPage : Page
{
    private ShellViewModel? _shell;

    public ReviewPage() => InitializeComponent();

    public ReviewViewModel ViewModel { get; private set; } = null!;

    /// <summary>The pressed one of the two view buttons is drawn as a bordered button, the other as a ghost (Review.png).</summary>
    public static ButtonVariant Pressed(bool pressed) => pressed ? ButtonVariant.Default : ButtonVariant.Ghost;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var composition = (AppComposition)e.Parameter;
        ViewModel = composition.Review;
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

    /// <summary>Up and Down on the file list select the previous or next file, and focus follows to its row.</summary>
    private void OnFileListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down))
            return;
        e.Handled = true;
        if (ViewModel.MoveSelection(e.Key == VirtualKey.Down ? 1 : -1) is not { } file)
            return;
        var index = ViewModel.Files.IndexOf(file);
        if (index >= 0 && FileList.ContainerFromIndex(index) is DependencyObject container && FindButton(container) is { } button)
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
        var layout = _shell?.Layout ?? ShellLayout.Wide;
        Diff.IsStacked = layout is ShellLayout.Narrow or ShellLayout.Phone;
        var phone = layout == ShellLayout.Phone;
        // The phone has no room for a 300 px column: the files go above the diff, full width.
        FilesColumn.Width = phone ? new GridLength(0) : new GridLength(300);
        Body.ColumnSpacing = phone ? 0 : 18;
        Body.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 18, 24, 18);
        Head.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);
        Grid.SetColumnSpan(FilesPanel, phone ? 2 : 1);
        Grid.SetRow(DiffArea, phone ? 1 : 0);
        Grid.SetColumn(DiffArea, phone ? 0 : 1);
        Grid.SetColumnSpan(DiffArea, phone ? 2 : 1);
    }
}
