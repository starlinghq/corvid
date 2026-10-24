using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Review;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Corvid.App.Controls;

/// <summary>
/// A file's change side by side (Review.dc.html <c>.diff-grid</c>), the diff view Review and Rewind share: the file
/// before on the left and after on the right, each its own column that scrolls sideways (<c>.side</c>, overflow auto), so
/// a comment under a right-hand line pushes only that column down and the left one's empty cells (<c>.dl.gap</c>) sit
/// beside lines only the right has. Lines are mono 12 px on 22 px rows with a 38 px number gutter; added lines on add-bg,
/// removed ones on del-bg, changed words highlighted. Under 860 px (<see cref="IsStacked"/>), or when the view itself
/// is under 640 px wide, the columns stack, before above after, as Review-narrow.png shows. With <see cref="IsUnified"/> it draws the unified rows in one column instead.
/// <para>
/// Building: every row is a few controls made on the UI thread, so at most <see cref="DiffRowsView.MaxRows"/> rows are
/// drawn and the rest are counted in one row; only the mode shown is built; and a burst of changes (a file selected sets
/// both row lists and the comments one by one) is drawn once, on the dispatcher's next turn. The comment field keeps its
/// <see cref="TextBox"/>, and its focus and selection, across those rebuilds.
/// </para>
/// <para>
/// Comments: a line is a focusable target (arrow keys move within a column, Enter or a click invokes
/// <see cref="LineCommand"/> with a <see cref="DiffLineRef"/>); <see cref="Comments"/> draw as the mockup's
/// <c>.comment</c> cards under their lines, and <see cref="Editor"/> as a field under its line. Without a
/// <see cref="LineCommand"/> (Rewind's preview) the lines are not focusable.
/// </para>
/// </summary>
public sealed partial class SideBySideDiffView : UserControl
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<SideBySideRow>), typeof(SideBySideDiffView), new PropertyMetadata(null, OnRowsChanged));

    public static readonly DependencyProperty UnifiedRowsProperty = DependencyProperty.Register(
        nameof(UnifiedRows), typeof(IReadOnlyList<DiffRow>), typeof(SideBySideDiffView), new PropertyMetadata(null, OnUnifiedRowsChanged));

    public static readonly DependencyProperty IsUnifiedProperty = DependencyProperty.Register(
        nameof(IsUnified), typeof(bool), typeof(SideBySideDiffView), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty IsStackedProperty = DependencyProperty.Register(
        nameof(IsStacked), typeof(bool), typeof(SideBySideDiffView), new PropertyMetadata(false, OnChanged));

    public static readonly DependencyProperty CommentsProperty = DependencyProperty.Register(
        nameof(Comments), typeof(IEnumerable), typeof(SideBySideDiffView), new PropertyMetadata(null, OnCommentsChanged));

    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(CommentEditor), typeof(SideBySideDiffView), new PropertyMetadata(null, OnEditorChanged));

    public static readonly DependencyProperty LineCommandProperty = DependencyProperty.Register(
        nameof(LineCommand), typeof(ICommand), typeof(SideBySideDiffView), new PropertyMetadata(null, OnChanged));

    private const double RowHeight = 22;
    private const double Gutter = 38;
    private const double NarrowWidth = 640;

    private readonly Grid _root = new();
    private readonly List<Column> _columns = [];
    private INotifyCollectionChanged? _observed;
    private TextBox? _editorBox;
    private CommentEditor? _editorBoxFor;
    private bool _narrow;
    private bool _rebuildPending;
    private bool _focusEditor;

    private bool Stacked => IsStacked || _narrow;

    public SideBySideDiffView()
    {
        IsTabStop = false;
        Content = _root;
        ActualThemeChanged += (_, _) => RequestRebuild();
        // Two columns under 640 px would show a few characters each; they stack instead, as under 860 px.
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < NarrowWidth;
            if (narrow == _narrow)
                return;
            _narrow = narrow;
            RequestRebuild();
        };
        // The comments collection outlives the page (it is the view model's); the view listens only while it is shown,
        // so a page left behind is not kept alive by it.
        Loaded += (_, _) =>
        {
            Observe(Comments);
            RequestRebuild();
        };
        Unloaded += (_, _) => Observe(null);
    }

    public IReadOnlyList<SideBySideRow>? Rows
    {
        get => (IReadOnlyList<SideBySideRow>?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public IReadOnlyList<DiffRow>? UnifiedRows
    {
        get => (IReadOnlyList<DiffRow>?)GetValue(UnifiedRowsProperty);
        set => SetValue(UnifiedRowsProperty, value);
    }

    public bool IsUnified
    {
        get => (bool)GetValue(IsUnifiedProperty);
        set => SetValue(IsUnifiedProperty, value);
    }

    /// <summary>Before above after rather than beside it: the narrow layouts.</summary>
    public bool IsStacked
    {
        get => (bool)GetValue(IsStackedProperty);
        set => SetValue(IsStackedProperty, value);
    }

    /// <summary>The comments to draw, <see cref="ReviewCommentView"/>s; a collection that changes redraws them.</summary>
    public IEnumerable? Comments
    {
        get => (IEnumerable?)GetValue(CommentsProperty);
        set => SetValue(CommentsProperty, value);
    }

    public CommentEditor? Editor
    {
        get => (CommentEditor?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>Invoked with a <see cref="DiffLineRef"/> when a line is clicked or Enter is pressed on it.</summary>
    public ICommand? LineCommand
    {
        get => (ICommand?)GetValue(LineCommandProperty);
        set => SetValue(LineCommandProperty, value);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SideBySideDiffView)d).RequestRebuild();

    /// <summary>The side-by-side rows: drawn only while they are the mode shown.</summary>
    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SideBySideDiffView)d;
        if (!view.IsUnified)
            view.RequestRebuild();
    }

    /// <summary>The unified rows: drawn only while they are the mode shown.</summary>
    private static void OnUnifiedRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SideBySideDiffView)d;
        if (view.IsUnified)
            view.RequestRebuild();
    }

    private static void OnCommentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SideBySideDiffView)d;
        // Set before the view is loaded (x:Bind's first update), it is observed from Loaded on.
        view.Observe(view.IsLoaded ? e.NewValue as IEnumerable : null);
        view.RequestRebuild();
    }

    private static void OnEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SideBySideDiffView)d;
        // A new field takes focus once it is drawn.
        view._focusEditor = e.NewValue is not null;
        view.RequestRebuild();
    }

    /// <summary>Listens to the comments collection's changes, or (with null) to none.</summary>
    private void Observe(IEnumerable? comments)
    {
        var next = comments as INotifyCollectionChanged;
        if (ReferenceEquals(next, _observed))
            return;
        if (_observed is not null)
            _observed.CollectionChanged -= OnCommentsCollectionChanged;
        _observed = next;
        if (_observed is not null)
            _observed.CollectionChanged += OnCommentsCollectionChanged;
    }

    private void OnCommentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RequestRebuild();

    /// <summary>Draws the view again on the dispatcher's next turn, once however many changes ask for it before then.</summary>
    private void RequestRebuild()
    {
        if (_rebuildPending)
            return;
        _rebuildPending = true;
        if (DispatcherQueue?.TryEnqueue(Rebuild) != true)
            Rebuild();
    }

    private void Rebuild()
    {
        _rebuildPending = false;
        // The field being typed in survives the rebuild (a file reloaded under it, a resize, a theme switch): the same
        // box goes into the new tree, and its focus and selection come back.
        var box = _editorBox;
        var hadFocus = box is not null && box.FocusState != FocusState.Unfocused;
        var (start, length) = box is null ? (0, 0) : (box.SelectionStart, box.SelectionLength);
        if (!ReferenceEquals(_editorBoxFor, Editor))
        {
            _editorBox = null;
            _editorBoxFor = null;
        }

        Build();

        var focus = _focusEditor || hadFocus;
        _focusEditor = false;
        if (focus && _editorBox is { } field)
        {
            // The field is in the tree after this layout pass.
            DispatcherQueue.TryEnqueue(() =>
            {
                field.Focus(FocusState.Programmatic);
                if (hadFocus && ReferenceEquals(field, box))
                    field.Select(Math.Min(start, field.Text.Length), Math.Min(length, Math.Max(0, field.Text.Length - start)));
            });
        }
    }

    private void Build()
    {
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        _root.RowDefinitions.Clear();
        _columns.Clear();
        var comments = (Comments ?? Array.Empty<ReviewCommentView>()).OfType<ReviewCommentView>().ToList();

        if (IsUnified)
        {
            var column = new Column(this, "Changes");
            var unified = UnifiedRows ?? [];
            foreach (var row in unified.Take(DiffRowsView.MaxRows))
            {
                if (row.Kind == DiffRowKind.Skip)
                {
                    column.AddFold(row.SkipText);
                    continue;
                }

                var side = row.Kind == DiffRowKind.Removed ? DiffSide.Old : DiffSide.New;
                var number = side == DiffSide.Old ? row.OldNumber : row.NewNumber;
                column.AddLine(side, number ?? 0, row.Kind, row.Segments, row.Text);
                // A comment on either side lands under the line that carries that side's number.
                if (row.OldNumber is { } old && row.Kind != DiffRowKind.Added)
                    column.AddNotes(comments, DiffSide.Old, old);
                if (row.NewNumber is { } current && row.Kind != DiffRowKind.Removed)
                    column.AddNotes(comments, DiffSide.New, current);
            }

            if (unified.Count > DiffRowsView.MaxRows)
                column.AddFold(MoreLines(unified.Skip(DiffRowsView.MaxRows).Count(row => row.Kind != DiffRowKind.Skip)));
            Place(column, 0);
            return;
        }

        var before = new Column(this, "Before");
        var after = new Column(this, "After");
        var rows = Rows ?? [];
        foreach (var row in rows.Take(DiffRowsView.MaxRows))
        {
            if (row.IsSkip)
            {
                var text = string.Create(CultureInfo.InvariantCulture, $"{row.Skipped} unchanged line{(row.Skipped == 1 ? "" : "s")}");
                before.AddFold(text);
                after.AddFold(text);
                continue;
            }

            var left = Add(before, row.Left, DiffSide.Old, comments);
            var right = Add(after, row.Right, DiffSide.New, comments);
            if (!Stacked && (left.Count > 0 || right.Count > 0))
                Mirror(before, left, after, right);
        }

        if (rows.Count > DiffRowsView.MaxRows)
        {
            var rest = rows.Skip(DiffRowsView.MaxRows).ToList();
            before.AddFold(MoreLines(rest.Count(row => row.Left is not null)));
            after.AddFold(MoreLines(rest.Count(row => row.Right is not null)));
        }

        Place(before, 0);
        Place(after, 1);
    }

    private static string MoreLines(int count) =>
        string.Create(CultureInfo.InvariantCulture, $"… {count} more line{(count == 1 ? "" : "s")}, not drawn here");

    private static List<FrameworkElement> Add(Column column, DiffCell? cell, DiffSide side, List<ReviewCommentView> comments)
    {
        if (cell is null)
        {
            column.AddGap();
            return [];
        }

        column.AddLine(side, cell.Number, cell.Kind, cell.Segments, cell.Text);
        return column.AddNotes(comments, side, cell.Number);
    }

    /// <summary>
    /// Side by side, the cards under a row take height in one column only; a blank of the same height in the other keeps
    /// the lines after them opposite their counterparts (old 52 beside new 56, not beside the lines the cards pushed down).
    /// </summary>
    private static void Mirror(Column before, List<FrameworkElement> left, Column after, List<FrameworkElement> right)
    {
        var leftSpace = before.AddSpace();
        var rightSpace = after.AddSpace();
        void Update()
        {
            var l = left.Sum(Outer);
            var r = right.Sum(Outer);
            leftSpace.Height = Math.Max(0, r - l);
            rightSpace.Height = Math.Max(0, l - r);
        }

        foreach (var card in left.Concat(right))
            card.SizeChanged += (_, _) => Update();
        Update();
    }

    private static double Outer(FrameworkElement element) =>
        element.Visibility == Visibility.Visible ? element.ActualHeight + element.Margin.Top + element.Margin.Bottom : 0;

    private void Place(Column column, int index)
    {
        _columns.Add(column);
        var element = column.Element;
        if (index > 0)
        {
            // .side + .side: a line between the columns, or above the second when they stack.
            element.BorderBrush = ThemeBrushes.Get(this, "CorvidLine");
            element.BorderThickness = Stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        }

        if (Stacked || IsUnified)
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(element, index);
        }
        else
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(element, index);
        }

        _root.Children.Add(element);
    }

    /// <summary>
    /// One column: a horizontal scroller over the lines. Comment cards fit the visible width and follow the scroll, so
    /// their text wraps at the column's edge however far the code scrolls (CSS keeps a block's width while its line
    /// content overflows).
    /// </summary>
    private sealed class Column
    {
        private readonly SideBySideDiffView _owner;
        private readonly StackPanel _lines = new();
        private readonly ScrollViewer _scroller;
        private readonly List<FrameworkElement> _cards = [];
        private readonly FontFamily _mono = (FontFamily)Application.Current.Resources["CorvidMono"];
        private double _cell;

        public Column(SideBySideDiffView owner, string name)
        {
            _owner = owner;
            _lines.TabFocusNavigation = KeyboardNavigationMode.Once;
            _lines.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
            AutomationProperties.SetName(_lines, name);
            _scroller = new ScrollViewer
            {
                Content = _lines,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                IsTabStop = false,
            };
            _scroller.SizeChanged += (_, _) => Fit();
            _scroller.ViewChanged += (_, _) => Follow();
            Element = new Border { Child = _scroller, MinWidth = 0 };
        }

        public Border Element { get; }

        public void AddLine(DiffSide side, int number, DiffRowKind kind, IReadOnlyList<DiffSegment> segments, string text)
        {
            var line = new DiffLine(_owner.LineCommand, new DiffLineRef(side, number, text))
            {
                MinHeight = RowHeight,
            };
            // The row's colour is on its content: a ContentControl's own Background is not drawn without a template that uses it.
            var grid = new Grid
            {
                MinHeight = RowHeight,
                Background = kind switch
                {
                    DiffRowKind.Added => ThemeBrushes.Get(_owner, "CorvidAddBg"),
                    DiffRowKind.Removed => ThemeBrushes.Get(_owner, "CorvidDelBg"),
                    _ => new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                },
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Number(number.ToString(CultureInfo.InvariantCulture), DiffRowsView.DiffSign(kind)));
            var words = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var highlight = kind == DiffRowKind.Removed ? ThemeBrushes.Get(_owner, "CorvidDelWord") : ThemeBrushes.Get(_owner, "CorvidAddWord");
            foreach (var segment in segments)
                words.Children.Add(Segment(segment.Text, segment.Changed && kind != DiffRowKind.Context ? highlight : null));
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            line.SetBody(grid, ThemeBrushes.Get(_owner, "CorvidAccent"));
            var sign = kind switch { DiffRowKind.Added => "added", DiffRowKind.Removed => "removed", _ => "line" };
            AutomationProperties.SetName(line, $"{sign} {number}: {text.Trim()}");
            _lines.Children.Add(line);
        }

        /// <summary>The empty cell beside a line only the other side has (<c>.dl.gap</c>).</summary>
        public void AddGap()
        {
            var gap = new Grid { Height = RowHeight, Background = ThemeBrushes.Get(_owner, "CorvidBg3") };
            gap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
            gap.Children.Add(Number(""));
            _lines.Children.Add(gap);
        }

        /// <summary>Unchanged lines left out between two hunks: a grey row that says how many.</summary>
        public void AddFold(string text)
        {
            var fold = new Grid { Height = RowHeight, Background = ThemeBrushes.Get(_owner, "CorvidBg3") };
            fold.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gutter) });
            fold.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fold.Children.Add(Number(""));
            var label = new TextBlock
            {
                Text = text,
                FontFamily = _mono,
                FontSize = 12,
                Foreground = ThemeBrushes.Get(_owner, "CorvidText2"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };
            Grid.SetColumn(label, 1);
            fold.Children.Add(label);
            AutomationProperties.SetName(fold, text);
            _lines.Children.Add(fold);
        }

        /// <summary>The comments on this side's line, then the open field if it is on this line; the cards added.</summary>
        public List<FrameworkElement> AddNotes(List<ReviewCommentView> comments, DiffSide side, int number)
        {
            var added = new List<FrameworkElement>();
            foreach (var comment in comments.Where(comment => comment.Side == side && comment.Line == number))
                added.Add(AddCard(CommentCard(comment)));
            if (_owner.Editor is { } editor && editor.Side == side && editor.Number == number)
                added.Add(AddCard(EditorCard(editor)));
            return added;
        }

        /// <summary>A blank as tall as the other column's cards beside it (<see cref="Mirror"/> sets its height).</summary>
        public FrameworkElement AddSpace()
        {
            var space = new Border { Height = 0, IsHitTestVisible = false };
            _lines.Children.Add(space);
            return space;
        }

        private Border AddCard(Border card)
        {
            // .comment: margin 4px 10px 8px 46px.
            card.Margin = new Thickness(46, 4, 10, 8);
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.RenderTransform = new TranslateTransform();
            _cards.Add(card);
            _lines.Children.Add(card);
            return card;
        }

        private Border Card() => new()
        {
            BorderBrush = ThemeBrushes.Get(_owner, "CorvidAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrushes.Get(_owner, "CorvidBg"),
            Padding = new Thickness(10, 8, 10, 8),
        };

        private Border CommentCard(ReviewCommentView comment)
        {
            var body = new StackPanel { Spacing = 6 };
            var meta = new Grid();
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            meta.Children.Add(new TextBlock { Text = comment.Meta, FontSize = 11.5, MinHeight = 16.7, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Foreground = ThemeBrushes.Get(_owner, "CorvidText2"), VerticalAlignment = VerticalAlignment.Center });
            var remove = new CorvidButton
            {
                Variant = ButtonVariant.Ghost,
                Height = 18,
                Padding = new Thickness(2, 0, 2, 0),
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                FontSize = 11.5,
                Foreground = ThemeBrushes.Get(_owner, "CorvidText2"),
                Command = comment.Remove,
                // .linkbtn's dotted underline, drawn as a faint rule: a TextBlock's underline does not show inside the button.
                Content = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = ThemeBrushes.Get(_owner, "CorvidText2"),
                    Opacity = 0.9,
                    Child = new TextBlock { Text = "remove", FontSize = 12 },
                },
                Margin = new Thickness(0, -2, -2, -2),
            };
            AutomationProperties.SetName(remove, $"Remove the comment on line {comment.Line}");
            Grid.SetColumn(remove, 1);
            meta.Children.Add(remove);
            body.Children.Add(meta);
            body.Children.Add(Prose(comment.Text));
            var card = Card();
            card.Child = body;
            AutomationProperties.SetName(card, comment.AutomationName);
            return card;
        }

        private Border EditorCard(CommentEditor editor)
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = editor.Title, FontSize = 11.5, Foreground = ThemeBrushes.Get(_owner, "CorvidText2") });
            body.Children.Add(EditorBox(editor));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(new CorvidButton { Content = "Add comment", Variant = ButtonVariant.Primary, Style = SmallButton(), Command = editor.Save });
            buttons.Children.Add(new CorvidButton { Content = "Cancel", Variant = ButtonVariant.Ghost, Style = SmallButton(), Command = editor.Cancel });
            body.Children.Add(buttons);
            var card = Card();
            card.Child = body;
            return card;
        }

        /// <summary>The field of this comment: the one already made for it (taken out of the old tree), or a new one.</summary>
        private TextBox EditorBox(CommentEditor editor)
        {
            if (ReferenceEquals(_owner._editorBoxFor, editor) && _owner._editorBox is { } existing)
            {
                if (existing.Parent is Panel parent)
                    parent.Children.Remove(existing);
                return existing;
            }

            var box = new TextBox
            {
                Text = editor.Text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 52,
                FontSize = 12.5,
                PlaceholderText = "Say what to change; Ctrl+Enter adds it, Esc cancels",
            };
            AutomationProperties.SetName(box, editor.Title);
            var saving = false;
            box.TextChanged += (_, _) => editor.Text = box.Text;
            box.KeyDown += (_, e) =>
            {
                var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (e.Key == VirtualKey.Enter && control)
                {
                    saving = true;
                    e.Handled = true;
                    editor.Save.Execute(null);
                }
                else if (e.Key == VirtualKey.Escape)
                {
                    e.Handled = true;
                    editor.Cancel.Execute(null);
                }
            };
            // Uno still types the line break of a handled Ctrl+Enter; that one change is not the person's.
            box.BeforeTextChanging += (_, e) =>
            {
                if (saving)
                    e.Cancel = true;
            };
            _owner._editorBox = box;
            _owner._editorBoxFor = editor;
            return box;
        }

        private static Style SmallButton() => (Style)Application.Current.Resources["CorvidSmallButton"];

        /// <summary>The comment's prose at 12.5 px, with its <c>`code`</c> spans in mono.</summary>
        private TextBlock Prose(string text)
        {
            var block = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushes.Get(_owner, "CorvidText"), LineHeight = 19.5 };
            var parts = text.Split('`');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                    continue;
                var run = new Run { Text = parts[i] };
                if (i % 2 == 1)
                    run.FontFamily = _mono;
                block.Inlines.Add(run);
            }

            return block;
        }

        /// <summary>The line number, with the sign ("+", "−") at the gutter's left so a change is not told by colour alone.</summary>
        private Border Number(string text, string sign = "")
        {
            var numbers = new Grid();
            if (sign.Length > 0)
            {
                numbers.Children.Add(new TextBlock
                {
                    Text = sign,
                    FontFamily = _mono,
                    FontSize = 12,
                    Foreground = ThemeBrushes.Get(_owner, "CorvidText2"),
                    Margin = new Thickness(3, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            numbers.Children.Add(new TextBlock
            {
                Text = text,
                FontFamily = _mono,
                FontSize = 12,
                Foreground = ThemeBrushes.Get(_owner, "CorvidText2"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return new Border
            {
                BorderBrush = ThemeBrushes.Get(_owner, "CorvidLine"),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(0, 0, 8, 0),
                Child = numbers,
            };
        }

        private Border Segment(string text, Brush? highlight)
        {
            // A run of only spaces would measure as nothing (Uno trims trailing white space, no-break or not), so the
            // indentation before a changed word keeps its width as an empty box of so many mono cells.
            if (text.Length > 0 && text.All(c => c is ' ' or '\t'))
            {
                var cells = text.Sum(c => c == '\t' ? 4 : 1);
                return new Border { Background = highlight, CornerRadius = new CornerRadius(2), Width = cells * CellWidth(), Height = 1 };
            }

            return Text(text, highlight);
        }

        /// <summary>The width of one JetBrains Mono cell at 12 px, measured once.</summary>
        private double CellWidth()
        {
            if (_cell > 0)
                return _cell;
            var probe = new TextBlock { Text = "0000000000", FontFamily = _mono, FontSize = 12 };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _cell = probe.DesiredSize.Width > 0 ? probe.DesiredSize.Width / 10 : 7.2;
            return _cell;
        }

        private Border Text(string text, Brush? highlight) => new()
        {
            Background = highlight,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                // Leading and trailing spaces keep their width as no-break spaces.
                Text = text.Replace(' ', ' ').Replace("\t", "    ", StringComparison.Ordinal),
                FontFamily = _mono,
                FontSize = 12,
                Foreground = ThemeBrushes.Get(_owner, "CorvidText"),
                TextWrapping = TextWrapping.NoWrap,
            },
        };

        /// <summary>Lines span at least the visible width (their backgrounds reach the edge); cards fit inside it.</summary>
        private void Fit()
        {
            var width = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : _scroller.ActualWidth;
            if (width <= 0)
                return;
            _lines.MinWidth = width;
            foreach (var card in _cards)
                card.Width = Math.Max(120, width - 56);
            Follow();
        }

        private void Follow()
        {
            foreach (var card in _cards)
            {
                if (card.RenderTransform is TranslateTransform move)
                    move.X = _scroller.HorizontalOffset;
            }
        }
    }

    /// <summary>
    /// A diff line that takes comments: focusable when there is a command, invoked by a click or Enter, with the hand
    /// cursor over it. Arrow keys move between lines of a column (XY focus).
    /// </summary>
    private sealed partial class DiffLine : ContentControl
    {
        private readonly ICommand? _command;
        private readonly DiffLineRef _line;

        public DiffLine(ICommand? command, DiffLineRef line)
        {
            _command = command;
            _line = line;
            IsTabStop = command is not null;
            // The system focus visual leaves keyboard-visited rows drawn faded on Skia; the line draws its own ring.
            UseSystemFocusVisuals = false;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            if (command is null)
                return;
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
            Tapped += (_, e) =>
            {
                e.Handled = true;
                Invoke();
            };
            KeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    e.Handled = true;
                    Invoke();
                }
            };
        }

        /// <summary>The line's content under a 2 px accent ring that shows while the keyboard is on it.</summary>
        public void SetBody(UIElement body, Brush ring)
        {
            var frame = new Border { BorderBrush = ring, BorderThickness = new Thickness(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var host = new Grid();
            host.Children.Add(body);
            host.Children.Add(frame);
            Content = host;
            GotFocus += (_, _) => frame.Visibility = FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed;
            LostFocus += (_, _) => frame.Visibility = Visibility.Collapsed;
        }

        private void Invoke()
        {
            if (_command?.CanExecute(_line) == true)
                _command.Execute(_line);
        }
    }
}
