using Corvid.App.Core.Composition;
using Corvid.App.Core.Safety;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Safety area's page. Its view model comes from the composition the shell navigates with. The page arranges itself
/// for the width (two columns, one on a phone), takes Delete on a rule's Remove… to ask about removing it and Esc to stop
/// asking, and puts focus on the confirmation's Remove when it opens, so the keyboard can answer it.
/// </summary>
public sealed partial class SafetyPage : Page
{
    private const double PhoneWidth = 720;

    public SafetyPage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        Loaded += OnLoaded;
        KeyDown += OnPageKeyDown;
        IsTabStop = true;
    }

    public SafetyViewModel ViewModel { get; private set; } = null!;

    /// <summary>"Remove the rule dotnet test in workspace…", the button's name for a screen reader.</summary>
    public static string RemoveName(string rule) => $"Remove the rule {rule}…";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (ViewModel is not null)
            ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel = ((AppComposition)e.Parameter).Safety;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // A question left open is not answered by leaving.
        if (ViewModel is not null)
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.CancelConfirm();
        }

        Bindings.StopTracking();
        base.OnNavigatedFrom(e);
    }

    /// <summary>The window's first focus would land on the first mode segment with a ring; the page takes it instead.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Arrange();
        Focus(FocusState.Programmatic);
    }

    private void Arrange()
    {
        var width = ActualWidth;
        if (width <= 0)
            return;
        var phone = width < PhoneWidth;
        Body.Padding = phone ? new Thickness(14, 14, 14, 18) : new Thickness(24, 18, 24, 18);
        HeadHost.Padding = phone ? new Thickness(14, 12, 14, 12) : new Thickness(24, 12, 24, 12);
        LeftColumn.Width = new GridLength(1.4, GridUnitType.Star);
        RightColumn.Width = phone ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Columns.ColumnSpacing = phone ? 0 : 18;
        Grid.SetColumn(RightStack, phone ? 0 : 1);
        Grid.SetRow(RightStack, phone ? 1 : 0);
    }

    /// <summary>Delete on a rule's Remove… asks; Esc on its question stops asking and puts focus back on Remove….</summary>
    private void OnRuleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RuleRowView row })
            return;
        switch (e.Key)
        {
            case VirtualKey.Delete when row.IsIdle:
                e.Handled = true;
                row.StartRemoveCommand.Execute(null);
                break;
            case VirtualKey.Escape when row.IsConfirming:
                e.Handled = true;
                row.IsConfirming = false;
                FocusRemove(sender as DependencyObject);
                break;
        }
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.CancelConfirm())
            e.Handled = true;
    }

    /// <summary>A rule's question opened: its "Remove the rule" takes focus, so Enter removes and Esc keeps.</summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SafetyViewModel.Confirming) || ViewModel.Confirming is not { } row)
            return;
        // The question's buttons are drawn in the next layout pass.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Find<Controls.CorvidButton>(RulesList, b => ReferenceEquals(b.Tag, row) && b.Content as string == "Remove the rule") is { } remove)
                remove.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>After a question closes, focus goes back to its row's Remove…, not to the next item's first button.</summary>
    private void FocusRemove(DependencyObject? from)
    {
        var parent = from;
        // Up to the row's container in the list, which holds both the question and Remove….
        while (parent is not null and not ContentPresenter)
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (parent is not null && Find<Controls.CorvidButton>(parent, b => b.Content as string == "Remove…") is { } remove)
                remove.Focus(FocusState.Keyboard);
            else
                Focus(FocusState.Programmatic);
        });
    }

    private static T? Find<T>(DependencyObject parent, Func<T, bool> match) where T : class, DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && match(typed))
                return typed;
            if (Find(child, match) is { } found)
                return found;
        }

        return null;
    }
}
