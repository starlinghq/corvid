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
        Root.KeyDown += OnRootKeyDown;
    }

    public JobsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = ((AppComposition)e.Parameter).Jobs;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
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
                DispatcherQueue.TryEnqueue(() => DraftNameBox.Focus(FocusState.Programmatic));
                break;
            case nameof(JobsViewModel.IsConfirmingRemove) when ViewModel.IsConfirmingRemove:
                DispatcherQueue.TryEnqueue(() => ConfirmRemoveButton.Focus(FocusState.Programmatic));
                break;
        }
    }

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
