using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Jobs;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Jobs area's page. Its view model comes from the composition the shell navigates with. The page arranges itself
/// for the width (<see cref="OpsLayout"/>), moves between jobs with Up and Down, closes the remove question or the form
/// with Esc, and puts the caret in the name field when the form opens.
/// </summary>
public sealed partial class JobsPage : Page
{
    public JobsPage()
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

    public JobsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = ((AppComposition)e.Parameter).Jobs;
        Bindings.Update();
        // After the bindings, so a control the change shows is visible when the handler looks for it.
        ViewModel.PropertyChanged += OnViewModelChanged;
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        base.OnNavigatedFrom(e);
    }

    private void Arrange() =>
        OpsLayout.Arrange(ActualWidth, HeadHost, HeadTitle, HeadButtons, Body, Columns, RightColumn, RightStack);

    private void OnTableSized(object sender, SizeChangedEventArgs e) => OpsLayout.FitTable(sender, e);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(JobsViewModel.IsCreating) when ViewModel.IsCreating:
                // The form opens above the tables: show it and put the caret in its first field.
                BodyScroll.ChangeView(null, 0, null, disableAnimation: true);
                FocusSoon(DraftNameBox, FocusState.Programmatic);
                break;
            case nameof(JobsViewModel.IsConfirmingRemove) when ViewModel.IsConfirmingRemove:
                FocusSoon(ConfirmRemoveButton, FocusState.Keyboard);
                break;
            case nameof(JobsViewModel.Selected):
                // A removed job's buttons go with it: focus would fall to the next tab stop (the status bar), so it goes
                // to the job now selected instead.
                DispatcherQueue.TryEnqueue(() =>
                {
                    var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
                    if (!IsInPage(focused))
                        FocusSelected();
                });
                break;
        }
    }

    /// <summary>Focuses a control a change just showed, once it is laid out (Uno refuses focus to one not yet measured).</summary>
    private static void FocusSoon(Control control, FocusState state) => OpsLayout.FocusWhenShown(control, state);

    /// <summary>Up and Down from a job row: the previous or next job, focused and in view.</summary>
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
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Button { DataContext: JobRowViewModel focused } && !ReferenceEquals(focused, ViewModel.Selected))
            focused.SelectCommand.Execute(null);
        ViewModel.MoveSelection(delta);
        FocusSelected();
        e.Handled = true;
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !e.Handled && ViewModel.Close())
            e.Handled = true;
    }

    private void FocusSelected()
    {
        if (ViewModel.Selected is { } row && JobRows.ContainerFromItem(row) is DependencyObject container && Find(container) is { } button)
        {
            button.Focus(FocusState.Keyboard);
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private bool IsInPage(DependencyObject? element)
    {
        for (var at = element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (ReferenceEquals(at, this))
                return true;
        }

        return false;
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
