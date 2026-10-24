using System.ComponentModel;
using Corvid.App.Core.Board;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Markdown;
using Corvid.App.Shell;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Corvid.App.Pages;

/// <summary>
/// The Board's page (Board.png): the session cards and a peek over them; the attention tray is <see cref="BoardDock"/> in
/// the shell's dock. The view model does the work; this class owns keys and focus.
/// <para>
/// Keys (docs/plan/ui.md §6, the <c>board</c> context): on a card the arrows move between cards across and down the
/// grid, Enter opens the session (a job's run), Space peeks; Esc closes the peek and puts focus back on its card.
/// </para>
/// </summary>
public sealed partial class BoardPage : Page, IDockProvider
{
    // Made with the page: the frame raises Navigated, where the shell takes the dock, before OnNavigatedTo gives the page
    // its view model, so the dock gets the view model there.
    private readonly BoardDock _dock = new();
    private SessionCardViewModel? _peeked;

    public BoardPage()
    {
        InitializeComponent();
        CardGrid.AddHandler(KeyDownEvent, new KeyEventHandler(OnCardKeyDown), true);
        Root.KeyDown += OnRootKeyDown;
    }

    public BoardViewModel ViewModel { get; private set; } = null!;

    public UIElement? Dock => _dock;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel = ((AppComposition)e.Parameter).Board;
        _dock.ViewModel = ViewModel;
        ViewModel.PropertyChanged += OnViewModelChanged;
        Bindings.Update();
        base.OnNavigatedTo(e);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelChanged;
        // A peek belongs to the visit; coming back shows the Board.
        ViewModel.ClosePeekCommand.Execute(null);
        base.OnNavigatedFrom(e);
    }

    // ---- x:Bind helpers ----

    /// <summary>What follows a card's state, after the mockup's dot: "· approval 1 of 2 · 4:12 left".</summary>
    public static IReadOnlyList<MarkdownRun> Dotted(IReadOnlyList<MarkdownRun>? runs) =>
        runs is null || runs.Count == 0 ? [] : [new MarkdownRun("· "), .. runs];

    public static string PeekState(string? state, string? detail) => BoardText.Join(state, detail);

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
            DispatcherQueue.TryEnqueue(() => FocusCard(ViewModel.Tiles.IndexOf(card)));
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

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.IsPeeking)
        {
            ViewModel.ClosePeekCommand.Execute(null);
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
        var index = ViewModel.Tiles.IndexOf(card);
        var columns = Columns();
        switch (e.Key)
        {
            case VirtualKey.Left:
                e.Handled = FocusCard(index - 1);
                break;
            case VirtualKey.Right:
                e.Handled = FocusCard(index + 1);
                break;
            case VirtualKey.Up:
                e.Handled = FocusCard(index - columns);
                break;
            case VirtualKey.Down:
                e.Handled = FocusCard(index + columns);
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

    /// <summary>How many columns the grid draws now (ColumnsPanel drops to one when a column would be too narrow).</summary>
    private int Columns()
    {
        var width = CardGrid.ActualWidth;
        return width >= 290 * 2 + 14 ? 2 : 1;
    }

    /// <summary>Focuses the card at a tile index; false when there is none there (the dashed card is not a stop).</summary>
    private bool FocusCard(int index)
    {
        if (index < 0 || index >= ViewModel.Tiles.Count || ViewModel.Tiles[index] is not SessionCardViewModel)
            return false;
        if (CardGrid.ContainerFromIndex(index) is not DependencyObject container || Find<ContentControl>(container) is not { } host)
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
