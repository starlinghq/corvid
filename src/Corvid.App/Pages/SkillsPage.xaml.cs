using System.ComponentModel;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Skills;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Skills area's page (ui.md §4.9, flow F12; drawn in the mocked pages' language, as no mockup exists): the drafts
/// beside the most used, the idle skills and the import; the library and the history as tables beside the open row; the
/// import's dry run. The view model does the work; this class owns keys, focus and the widths at which the columns stack.
/// <para>
/// Keys: Ctrl+F puts the caret in the filter (Esc there clears it); Ctrl+1 to Ctrl+4 pick the tabs; in the library and
/// the history Up and Down open the next or previous row and Enter or Space opens the focused one; Esc closes the open
/// skill or commit. In a draft's editor Ctrl+Enter approves as edited and Esc undoes the edit; in a reason Enter rejects
/// and Esc goes back; in the import's fields Enter runs the dry run.
/// </para>
/// </summary>
public sealed partial class SkillsPage : Page
{
    /// <summary>Below this width the side panels go under the main column (the phone layout, and a narrow pane).</summary>
    private const double OneColumnWidth = OpsLayout.TwoColumnWidth;

    /// <summary>Below this width the gutters narrow to the phone's 14 px.</summary>
    private const double PhoneWidth = 720;

    /// <summary>A table narrower than this drops its secondary columns.</summary>
    private const double NarrowTableWidth = 520;

    private static readonly string[] EditorModes = [nameof(SkillDraftView.IsEditing), nameof(SkillDraftView.IsRejecting)];

    private bool _oneColumn;

    public SkillsPage()
    {
        InitializeComponent();
        AddKey(VirtualKey.F, VirtualKeyModifiers.Control, () => FilterBox.Focus(FocusState.Keyboard));
        foreach (var tab in Enum.GetValues<SkillsTabKind>())
            AddKey(VirtualKey.Number1 + (int)tab, VirtualKeyModifiers.Control, () => ViewModel?.Select(tab));
        Root.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        TabStrip.SizeChanged += (_, _) => ApplyWidth(Root.ActualWidth);
        // The window's first focus would otherwise land on the draft's Approve and draw its ring; the page takes it.
        IsTabStop = true;
        Loaded += (_, _) =>
        {
            if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is null or Button)
                Focus(FocusState.Programmatic);
        };
    }

    public SkillsViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Skills;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        // Pages are not cached: this one lets go of the long-lived view model.
        Bindings.StopTracking();
        base.OnNavigatedFrom(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>"· the description", or nothing; the space before it is the runs' own.</summary>
    public static string Dotted(string? text) => string.IsNullOrEmpty(text) ? "" : "· " + text;

    /// <summary>A detail fact's font: monospace for revisions, folders and tools.</summary>
    public static FontFamily FactFont(bool isCode) =>
        (FontFamily)Application.Current.Resources[isCode ? "CorvidMono" : "CorvidSans"];

    // ---- layout ----

    private void ApplyWidth(double width)
    {
        var oneColumn = width < OneColumnWidth;
        var phone = width < PhoneWidth;
        var gutter = phone ? 14 : 24;
        _oneColumn = oneColumn;
        // The phone's top bar names the page already.
        PageTitle.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        Body.Padding = new Thickness(gutter, 18, gutter, 18);
        Notices.Padding = new Thickness(gutter, 14, gutter, 0);
        Head.Padding = new Thickness(gutter, 12, gutter, 12);
        LeftColumn.Width = new GridLength(oneColumn ? 1 : 1.4, GridUnitType.Star);
        RightColumn.Width = oneColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Body.ColumnSpacing = oneColumn ? 0 : 18;
        foreach (var side in new FrameworkElement[] { ReviewSide, LibrarySide, HistorySide })
        {
            Grid.SetColumn(side, oneColumn ? 0 : 1);
            Grid.SetRow(side, oneColumn ? 1 : 0);
        }

        var inner = width - 2 * gutter;
        SkillsLayout.Current.Narrow = (oneColumn ? inner : (inner - 18) / 2.4 * 1.4) < NarrowTableWidth;
        // The filter is 300 px, as Memory's search; it gives up to 80 px to stay on the title's line, and below that takes
        // a line of its own, never wider than the head.
        var left = inner - PageTitle.ActualWidth - TabStrip.ActualWidth - 24;
        FilterBox.Width = left >= 220 ? Math.Min(300, left) : Math.Min(300, Math.Max(160, inner));
        PadTabs(TabStrip, phone ? new Thickness(6, 0, 6, 0) : new Thickness(9, 0, 9, 0));
    }

    private static void PadTabs(DependencyObject parent, Thickness padding)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Controls.DockTab tab)
            {
                if (tab.Padding != padding)
                    tab.Padding = padding;
            }
            else
            {
                PadTabs(child, padding);
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

    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.FilterText.Length > 0)
        {
            ViewModel.ClearFilterCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnImportKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        ViewModel.ScanCommand.Execute(null);
    }

    /// <summary>Up and Down in a table open the next or previous row and keep the focus on it.</summary>
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
        // From the focused row, not the open one: Tab may have moved focus without opening anything.
        if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Button { DataContext: var focused })
        {
            if (focused is SkillRowView skill && !ReferenceEquals(skill, ViewModel.SelectedSkill))
                ViewModel.SelectSkill(skill);
            else if (focused is CommitRowView commit && !ReferenceEquals(commit, ViewModel.SelectedCommit))
                ViewModel.SelectCommit(commit);
        }

        ViewModel.MoveSelection(delta);
        FocusSelectedRow();
        e.Handled = true;
    }

    /// <summary>
    /// Opening a row with the pointer when the side panels sit under the table (the phone, a narrow pane): show what
    /// opened, since it is not beside the row.
    /// </summary>
    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (!_oneColumn)
            return;
        var side = ViewModel.IsHistoryTab ? HistorySide : LibrarySide;
        // At low priority: the click's focus first brings the row into view, and this must come after it.
        _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var top = side.TransformToVisual(Body).TransformPoint(default).Y;
            Scroller.ChangeView(null, Math.Max(0, top - 12), null, true);
        });
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled || !ViewModel.HasOpenDetail)
            return;
        var row = ViewModel.IsHistoryTab ? (object?)ViewModel.SelectedCommit : ViewModel.SelectedSkill;
        ViewModel.CloseDetail();
        // Back to the row that was open, so Up and Down go on from there.
        if (row is not null && RowButton(row) is { } button)
            button.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void FocusSelectedRow()
    {
        object? row = ViewModel.IsHistoryTab ? ViewModel.SelectedCommit : ViewModel.SelectedSkill;
        if (row is not null && RowButton(row) is { } button)
        {
            button.Focus(FocusState.Keyboard);
            button.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        }
    }

    private Button? RowButton(object row)
    {
        var rows = row is CommitRowView ? CommitRows : SkillRows;
        return rows.ContainerFromItem(row) is DependencyObject container ? FindButton(container) : null;
    }

    private static Button? FindButton(DependencyObject parent)
    {
        if (parent is Button button)
            return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindButton(VisualTreeHelper.GetChild(parent, i)) is { } found)
                return found;
        }

        return null;
    }

    private void OnDraftEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SkillDraftView draft)
            return;
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter && control)
        {
            // Uno still types the line break of a handled Enter in a box that accepts returns (README): drop that change.
            _swallowLineBreak = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _swallowLineBreak = false);
            Confirm(e, draft.ApproveCommand);
        }
        else if (e.Key == VirtualKey.Escape)
            Confirm(e, draft.CancelEditCommand);
    }

    private bool _swallowLineBreak;

    private void OnDraftEditorTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        if (_swallowLineBreak && LineBreaks(args.NewText) > LineBreaks(sender.Text))
        {
            args.Cancel = true;
            _swallowLineBreak = false;
        }
    }

    private static int LineBreaks(string text) => text.Count(c => c is '\r' or '\n');

    private void OnReasonKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not SkillDraftView draft)
            return;
        if (e.Key == VirtualKey.Enter)
            Confirm(e, draft.ConfirmRejectCommand);
        else if (e.Key == VirtualKey.Escape)
            Confirm(e, draft.BackCommand);
    }

    private void Confirm(KeyRoutedEventArgs e, System.Windows.Input.ICommand command)
    {
        e.Handled = true;
        if (!command.CanExecute(null))
            return;
        // The editor is about to close (or its card to go): the page keeps the caret rather than the next card's first
        // button, which would light up as if chosen.
        Focus(FocusState.Programmatic);
        command.Execute(null);
    }

    /// <summary>
    /// An editor takes the caret when its card opens it (Edit…, Reject with a reason…). The card outlives the page, so the
    /// hook is kept on the editor's <c>Tag</c> and taken off when the editor unloads.
    /// </summary>
    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is EditorHook || box.DataContext is not SkillDraftView source)
            return;
        var dispatcher = DispatcherQueue;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is null || !EditorModes.Contains(args.PropertyName))
                return;
            dispatcher.TryEnqueue(() =>
            {
                var open = box.AcceptsReturn ? source.IsEditing : source.IsRejecting;
                if (!open || !box.IsLoaded)
                    return;
                box.Focus(FocusState.Programmatic);
                box.SelectionStart = box.AcceptsReturn ? 0 : box.Text.Length;
            });
        };
        source.PropertyChanged += handler;
        box.Tag = new EditorHook(source, handler);
    }

    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.Tag is not EditorHook hook)
            return;
        hook.Source.PropertyChanged -= hook.Handler;
        box.Tag = null;
    }

    /// <summary>What an editor subscribed to on its card, so it can let go.</summary>
    private sealed record EditorHook(INotifyPropertyChanged Source, PropertyChangedEventHandler Handler);
}
