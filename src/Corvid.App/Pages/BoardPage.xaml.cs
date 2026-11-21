using System.ComponentModel;
using Corvid.App.Controls;
using Corvid.App.Core.Board;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Markdown;
using Corvid.App.Core.Shell;
using Corvid.App.Shell;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Board's page (Board.png): the session cards in the kanban's columns (C-043) and a peek over them; the attention
/// tray is <see cref="BoardDock"/> in the shell's dock. The view model does the work; this class owns keys, focus and how
/// many columns fit across.
/// <para>
/// Keys (docs/plan/ui.md §6, the <c>board</c> context): on a card Up and Down move within its column and Left and Right to
/// the next column, Enter opens the session (a job's run), Space peeks; Esc closes the peek and puts focus back on its card. Ctrl+F,
/// or / outside a text field, puts the caret in the filter box (C-044), where Esc clears it.
/// </para>
/// </summary>
public sealed partial class BoardPage : Page, IDockProvider
{
    // Made with the page: the frame raises Navigated, where the shell takes the dock, before OnNavigatedTo gives the page
    // its view model, so the dock gets the view model there.
    private readonly BoardDock _dock = new();
    private AppComposition? _composition;
    private readonly KeyEventHandler _windowKeyDown;
    private SessionCardViewModel? _peeked;

    public BoardPage()
    {
        _windowKeyDown = OnWindowKeyDown;
        InitializeComponent();
        var find = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, args) =>
        {
            FocusFilter();
            args.Handled = true;
        };
        KeyboardAccelerators.Add(find);
        CardGrid.AddHandler(KeyDownEvent, new KeyEventHandler(OnCardKeyDown), true);
        CardGrid.SizeChanged += (_, _) => ApplyColumns();
        // Esc closes the peek wherever focus is, the tray in the dock included: the window's content hears it, since a key
        // pressed in the dock never reaches the page and the shell's own Esc accelerator answers first.
        Loaded += (_, _) => XamlRoot?.Content?.AddHandler(KeyDownEvent, _windowKeyDown, true);
        Unloaded += (_, _) => XamlRoot?.Content?.RemoveHandler(KeyDownEvent, _windowKeyDown);
    }

    public BoardViewModel ViewModel { get; private set; } = null!;

    public UIElement? Dock => _dock;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _composition = (AppComposition)e.Parameter;
        ViewModel = _composition.Board;
        _dock.ViewModel = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _composition.Shell.PropertyChanged += OnShellChanged;
        ApplyLayout();
        ApplyColumns();
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        if (_composition is not null)
            _composition.Shell.PropertyChanged -= OnShellChanged;
        // A peek belongs to the visit; coming back shows the Board.
        ViewModel.ClosePeekCommand.Execute(null);
        base.OnNavigatedFrom(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>
    /// A card's state line: the state in bold, then what follows it after the mockup's gap and dot ("<b>waiting on you</b>
    /// · approval 1 of 2 · 4:12 left"), in one block so a long target wraps under the state rather than beside it.
    /// </summary>
    public static IReadOnlyList<MarkdownRun> StateRuns(string? state, IReadOnlyList<MarkdownRun>? detail)
    {
        var runs = new List<MarkdownRun> { new(state ?? "", InlineStyle.Bold) };
        if (detail is { Count: > 0 })
        {
            // An en space and a space: the 8 px gap between the mockup's bold state and its dotted detail.
            runs.Add(new MarkdownRun("\u2002· "));
            runs.AddRange(detail);
        }

        return runs;
    }

    public static string PeekState(string? state, string? detail) => BoardText.Join(state, detail);

    /// <summary>A column head's fold button.</summary>
    public static string FoldText(bool collapsed) => collapsed ? "Show" : "Hide";

    // ---- Layout ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.Layout) or nameof(ShellViewModel.ShowDock))
            ApplyLayout();
    }

    /// <summary>
    /// On a phone (Phone.png's shell) the top bar names the page, so the head drops its title; the dock is gone, so the
    /// tray's items lead the body; and the gutters narrow to 14. x:Bind paths on the page do not follow the shell's layout
    /// here, so this is set in code.
    /// </summary>
    private void ApplyLayout()
    {
        var layout = _composition?.Shell.Layout ?? ShellLayout.Wide;
        var phone = layout == ShellLayout.Phone;
        PageTitle.Visibility = phone ? Visibility.Collapsed : Visibility.Visible;
        PhoneAttention.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
        // Without the dock column (under 1180 px, or hidden with Ctrl+B), the tray flows below the cards (Board-narrow.png).
        InlineAttention.Visibility = !phone && _composition?.Shell.ShowDock == false ? Visibility.Visible : Visibility.Collapsed;
        BodyPanel.Padding = phone ? new Thickness(14, 12, 14, 18) : new Thickness(24, 18, 24, 18);
        HeadBorder.Padding = phone ? new Thickness(14, 10, 14, 10) : new Thickness(24, 12, 24, 12);
    }

    /// <summary>The narrowest a column is drawn four across; two by two needs <see cref="PairWidth"/> each.</summary>
    private const double ColumnWidth = 180, PairWidth = 260, Gap = 14;

    private int _across;

    /// <summary>
    /// C-043: four columns across when they fit, two by two (Needs you and Running first) when two do, else stacked in the
    /// order Needs you, Running, Parked, Done.
    /// </summary>
    private void ApplyColumns()
    {
        if (ViewModel is null)
            return;
        var width = CardGrid.ActualWidth > 0 ? CardGrid.ActualWidth : BodyPanel.ActualWidth;
        var across = width <= 0 || width >= ColumnWidth * 4 + Gap * 3 ? 4 : width >= PairWidth * 2 + Gap ? 2 : 1;
        if (across == _across && CardGrid.ItemsSource is not null)
            return;
        _across = across;
        var columns = across == 4 ? ViewModel.Columns : ViewModel.Stacked;
        if (!ReferenceEquals(CardGrid.ItemsSource, columns))
            CardGrid.ItemsSource = columns;
        if (CardGrid.ItemsPanelRoot is ColumnsPanel panel)
            panel.Columns = across;
        else
            DispatcherQueue.TryEnqueue(() =>
            {
                if (CardGrid.ItemsPanelRoot is ColumnsPanel later)
                    later.Columns = _across;
            });
    }

    // ---- Peek ----

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BoardViewModel.Peek))
            return;
        if (ViewModel.Peek is { } peek)
        {
            _peeked = peek.Card;
            // Focus moves into the peek, so Esc and Tab act there; Close is always present.
            DispatcherQueue.TryEnqueue(() => PeekClose.Focus(FocusState.Programmatic));
        }
        else if (_peeked is { } card)
        {
            _peeked = null;
            DispatcherQueue.TryEnqueue(() => FocusCard(card));
        }
    }

    private void OnPeekScrimTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ClosePeekCommand.Execute(null);

    private void OnPeekOpen(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Peek?.Card is { CanOpen: true } card)
        {
            _peeked = null;
            ViewModel.ClosePeekCommand.Execute(null);
            card.OpenCommand.Execute(null);
        }
    }

    private void OnWindowKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // C-044: / outside a text field (Shift+/ is ? for help, the shell's) puts the caret in the filter box.
        if (e.Key == (VirtualKey)191 && !e.Handled && _composition?.Shell.ShowScrim != true
            && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
            && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is not (TextBox or PasswordBox or RichEditBox))
        {
            FocusFilter();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && ViewModel is { IsPeeking: true } && _composition?.Shell.ShowScrim != true)
        {
            ViewModel.ClosePeekCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- The filter box ----

    private void FocusFilter()
    {
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    }

    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.FilterText.Length > 0)
        {
            ViewModel.ClearFilterCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Cards ----

    private void OnCardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || CardOf(e.OriginalSource as DependencyObject) is not { } host || host.DataContext is not SessionCardViewModel card)
            return;
        // Keys on a card's own buttons are the buttons'; the grid's keys act on the card itself.
        if (!ReferenceEquals(e.OriginalSource, host))
            return;
        var columns = Shown();
        var column = columns.FindIndex(shown => shown.Cards.Contains(card));
        var index = column < 0 ? -1 : columns[column].Cards.IndexOf(card);
        switch (e.Key)
        {
            case VirtualKey.Left:
                e.Handled = FocusNear(columns, column, index, -1);
                break;
            case VirtualKey.Right:
                e.Handled = FocusNear(columns, column, index, +1);
                break;
            case VirtualKey.Up:
                e.Handled = column >= 0 && FocusCard(column, index - 1);
                break;
            case VirtualKey.Down:
                e.Handled = column >= 0 && FocusCard(column, index + 1);
                break;
            case VirtualKey.Enter:
                if (card.CanOpen)
                    card.OpenCommand.Execute(null);
                else if (card.IsJob)
                    card.OpenRunCommand.Execute(null);
                else if (card.CanPeek)
                    card.PeekCommand.Execute(null);
                e.Handled = true;
                break;
            case VirtualKey.Space:
                if (card.CanPeek)
                    card.PeekCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The columns in the order drawn now.</summary>
    private List<BoardColumnViewModel> Shown() => [.. (CardGrid.ItemsSource as IReadOnlyList<BoardColumnViewModel>) ?? ViewModel.Columns];

    /// <summary>Left or Right: the nearest card at the same height in the next column that shows one, skipping empty or folded ones.</summary>
    private bool FocusNear(List<BoardColumnViewModel> columns, int column, int index, int step)
    {
        for (var next = column + step; column >= 0 && next >= 0 && next < columns.Count; next += step)
        {
            if (columns[next].IsCollapsed || columns[next].Cards.Count == 0)
                continue;
            return FocusCard(next, Math.Min(Math.Max(index, 0), columns[next].Cards.Count - 1));
        }

        return false;
    }

    /// <summary>Focuses a card wherever it shows; false when it is not drawn (a folded column, past "Show N more").</summary>
    private bool FocusCard(SessionCardViewModel card)
    {
        var columns = Shown();
        var column = columns.FindIndex(shown => shown.Cards.Contains(card));
        return column >= 0 && FocusCard(column, columns[column].Cards.IndexOf(card));
    }

    /// <summary>Focuses the card at an index of a drawn column; false when there is none there.</summary>
    private bool FocusCard(int column, int index)
    {
        var columns = Shown();
        if (column < 0 || column >= columns.Count || index < 0 || index >= columns[column].Cards.Count || columns[column].IsCollapsed)
            return false;
        if (CardGrid.ContainerFromIndex(column) is not DependencyObject columnContainer
            || Find<ItemsControl>(columnContainer) is not { } cards
            || cards.ContainerFromIndex(index) is not DependencyObject container || Find<ContentControl>(container) is not { } host)
            return false;
        host.StartBringIntoView();
        return host.Focus(FocusState.Keyboard);
    }

    private static ContentControl? CardOf(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ContentControl { DataContext: SessionCardViewModel } host && current is not Button)
                return host;
        }

        return null;
    }

    private static T? Find<T>(DependencyObject root) where T : class
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && child is not Button)
                return found;
            if (Find<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }
}
