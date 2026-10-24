using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Memory;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Memory area's page (Memory.png): the review queue beside the core blocks, the recent facts and the import, and the
/// Facts, Core blocks, Lessons and Import tabs. The view model does the work; this class owns keys, focus and the width
/// at which the two columns become one.
/// <para>
/// Keys: Ctrl+F puts the caret in the search, where Enter searches the facts and Esc clears it; Ctrl+1 to Ctrl+5 pick
/// the tabs. In a correction or a reason, Enter confirms (approves the correction, rejects with the reason) and Esc
/// goes back to the staged text.
/// </para>
/// </summary>
public sealed partial class MemoryPage : Page
{
    /// <summary>Below this width the side panels go under the queue (the phone layout, and a narrow pane).</summary>
    private const double OneColumnWidth = 640;

    /// <summary>Below this width the gutters narrow to the phone's 14 px.</summary>
    private const double PhoneWidth = 720;

    private static readonly string[] EditorModes =
        [nameof(StagedFactView.IsCorrecting), nameof(BlockUpdateView.IsEditing), nameof(LessonCandidateView.IsRejecting)];

    public MemoryPage()
    {
        InitializeComponent();
        AddKey(VirtualKey.F, VirtualKeyModifiers.Control, () => SearchBox.Focus(FocusState.Keyboard));
        foreach (var tab in Enum.GetValues<MemoryTabKind>())
            AddKey(VirtualKey.Number1 + (int)tab, VirtualKeyModifiers.Control, () => ViewModel?.Select(tab));
        Root.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        TabStrip.SizeChanged += (_, _) => ApplyWidth(Root.ActualWidth);
        // The window's first focus would otherwise land on the first card's Approve and draw its ring (Memory.png has
        // none); the page itself takes it, which draws nothing, and Tab goes on to the tabs from there.
        IsTabStop = true;
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is null or Button)
                Focus(FocusState.Programmatic);
        };
    }

    public MemoryViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Memory;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>"· the scanner flagged a credential", or nothing; the space before it is the runs' own.</summary>
    public static string Dotted(string? text) => string.IsNullOrEmpty(text) ? "" : "· " + text;

    /// <summary>A date that may break after its hyphens, as the browser breaks the mockup's table cells ("2026-10-" over "16").</summary>
    public static string Breakable(string? text) => (text ?? "").Replace("-", "-​", StringComparison.Ordinal);

    // ---- layout ----

    private void ApplyWidth(double width)
    {
        var oneColumn = width < OneColumnWidth;
        var phone = width < PhoneWidth;
        var gutter = phone ? 14 : 24;
        // The phone's top bar names the page already.
        PageTitle.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        Body.Padding = new Thickness(gutter, 18, gutter, 18);
        Head.Padding = new Thickness(gutter, 12, gutter, 12);
        LeftColumn.Width = new GridLength(oneColumn ? 1 : 1.4, GridUnitType.Star);
        RightColumn.Width = oneColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = oneColumn ? 0 : 18;
        Grid.SetColumn(SideColumn, oneColumn ? 0 : 1);
        Grid.SetRow(SideColumn, oneColumn ? 1 : 0);
        var inner = width - 2 * gutter;
        MemoryLayout.Current.NarrowTable = (oneColumn ? inner : (inner - 18) / 2.4) < 400;
        // The search is 300 px (Memory.png). It gives up to 80 px to stay on the title's line, as the narrow render's
        // does; below that it takes a line of its own, never wider than the head.
        var left = inner - PageTitle.ActualWidth - TabStrip.ActualWidth - 24;
        SearchBox.Width = left >= 220 ? Math.Min(300, left) : Math.Min(300, Math.Max(160, inner));
        _tabPadding = phone ? new Thickness(6, 0, 6, 0) : new Thickness(9, 0, 9, 0);
        PadTabs();
    }

    private Thickness _tabPadding = new(9, 0, 9, 0);

    /// <summary>On a phone the five tabs keep to one line (362 px between the gutters) with 6 px sides instead of 9.</summary>
    private void PadTabs() => PadTabs(TabStrip);

    private void PadTabs(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Controls.DockTab tab)
            {
                if (tab.Padding != _tabPadding)
                    tab.Padding = _tabPadding;
            }
            else
            {
                PadTabs(child);
            }
        }
    }

    // ---- keys ----

    private void AddKey(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                ViewModel.SearchCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Escape when ViewModel.SearchText.Length > 0:
                ViewModel.ClearSearchCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnFactEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is StagedFactView fact)
            Confirm(e, fact.ApproveCommand, fact.CancelCorrectionCommand);
    }

    private void OnBlockEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is BlockUpdateView update)
            Confirm(e, update.ApproveCommand, update.CancelCommand);
    }

    private void OnReasonKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is LessonCandidateView lesson)
            Confirm(e, lesson.ConfirmRejectCommand, lesson.BackCommand);
    }

    private void Confirm(KeyRoutedEventArgs e, System.Windows.Input.ICommand enter, System.Windows.Input.ICommand escape)
    {
        var command = e.Key switch
        {
            VirtualKey.Enter => enter,
            VirtualKey.Escape => escape,
            _ => null,
        };
        if (command is null)
            return;
        e.Handled = true;
        if (!command.CanExecute(null))
            return;
        // The editor is about to close (or its card to go): the page keeps the caret rather than the next card's first
        // button, which would light up as if chosen.
        Focus(FocusState.Programmatic);
        command.Execute(null);
    }

    /// <summary>An editor takes the caret when its card opens it (Correct…, a consolidation, Reject with a reason…).</summary>
    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is not null || box.DataContext is not INotifyPropertyChanged source)
            return;
        box.Tag = source;
        source.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is null || !EditorModes.Contains(args.PropertyName))
                return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsOpen(source))
                    return;
                box.Focus(FocusState.Programmatic);
                box.SelectionStart = box.Text.Length;
            });
        };
    }

    private static bool IsOpen(object source) => source switch
    {
        StagedFactView fact => fact.IsCorrecting,
        BlockUpdateView update => update.IsEditing,
        LessonCandidateView lesson => lesson.IsRejecting,
        _ => false,
    };

    /// <summary>The operation select: a menu of add, update, invalidate and noop with the one in force checked.</summary>
    private void OnOperationClick(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        if (button.DataContext is not StagedFactView fact)
            return;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var choice in fact.Operations)
        {
            var item = new ToggleMenuFlyoutItem { Text = choice.Label, IsChecked = choice.IsSelected, Command = choice.Choose };
            ToolTipService.SetToolTip(item, choice.Description);
            menu.Items.Add(item);
        }

        menu.ShowAt(button);
    }
}
