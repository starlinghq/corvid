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
/// Comments: a line is a focusable target (arrow keys move within a column, Enter or a click invokes
/// <see cref="LineCommand"/> with a <see cref="DiffLineRef"/>); <see cref="Comments"/> draw as the mockup's
/// <c>.comment</c> cards under their lines, and <see cref="Editor"/> as a field under its line. Without a
/// <see cref="LineCommand"/> (Rewind's preview) the lines are not focusable.
/// </para>
/// </summary>
public sealed partial class SideBySideDiffView : UserControl
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IReadOnlyList<SideBySideRow>), typeof(SideBySideDiffView), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty UnifiedRowsProperty = DependencyProperty.Register(
        nameof(UnifiedRows), typeof(IReadOnlyList<DiffRow>), typeof(SideBySideDiffView), new PropertyMetadata(null, OnChanged));

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
    private bool _narrow;

    private bool Stacked => IsStacked || _narrow;

    public SideBySideDiffView()
    {
        IsTabStop = false;
        Content = _root;
        ActualThemeChanged += (_, _) => Rebuild();
        // Two columns under 640 px would show a few characters each; they stack instead, as under 860 px.
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < NarrowWidth;
            if (narrow == _narrow)
                return;
            _narrow = narrow;
            Rebuild();
        };
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

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SideBySideDiffView)d).Rebuild();

    private static void OnCommentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SideBySideDiffView)d;
        if (view._observed is not null)
            view._observed.CollectionChanged -= view.OnCommentsCollectionChanged;
        view._observed = e.NewValue as INotifyCollectionChanged;
        if (view._observed is not null)
            view._observed.CollectionChanged += view.OnCommentsCollectionChanged;
        view.Rebuild();
    }

    private static void OnEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (SideBySideDiffView)d;
        view.Rebuild();
        if (e.NewValue is not null)
            view.FocusEditor();
    }

    private void OnCommentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void FocusEditor()
    {
        var box = _editorBox;
        if (box is null)
            return;
        // The field is in the tree after this layout pass.
        DispatcherQueue.TryEnqueue(() => box.Focus(FocusState.Programmatic));
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        _root.RowDefinitions.Clear();
        _columns.Clear();
        _editorBox = null;
        var comments = (Comments ?? Array.Empty<ReviewCommentView>()).OfType<ReviewCommentView>().ToList();

        if (IsUnified)
        {
            var column = new Column(this, "Changes");
            foreach (var row in UnifiedRows ?? [])
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

            Place(column, 0);
            return;
        }

        var before = new Column(this, "Before");
        var after = new Column(this, "After");
        foreach (var row in Rows ?? [])
        {
            if (row.IsSkip)
            {
                var text = string.Create(CultureInfo.InvariantCulture, $"{row.Skipped} unchanged line{(row.Skipped == 1 ? "" : "s")}");
                before.AddFold(text);
                after.AddFold(text);
                continue;
            }

            Add(before, row.Left, DiffSide.Old, comments);
            Add(after, row.Right, DiffSide.New, comments);
        }

        Place(before, 0);
        Place(after, 1);
    }

    private static void Add(Column column, DiffCell? cell, DiffSide side, List<ReviewCommentView> comments)
    {
        if (cell is null)
        {
            column.AddGap();
            return;
        }

        column.AddLine(side, cell.Number, cell.Kind, cell.Segments, cell.Text);
        column.AddNotes(comments, side, cell.Number);
    }

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
            grid.Children.Add(Number(number.ToString(CultureInfo.InvariantCulture)));
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

        /// <summary>The comments on this side's line, then the open field if it is on this line.</summary>
        public void AddNotes(List<ReviewCommentView> comments, DiffSide side, int number)
        {
            foreach (var comment in comments.Where(comment => comment.Side == side && comment.Line == number))
                AddCard(CommentCard(comment));
            if (_owner.Editor is { } editor && editor.Side == side && editor.Number == number)
                AddCard(EditorCard(editor));
        }

        private void AddCard(Border card)
        {
            // .comment: margin 4px 10px 8px 46px.
            card.Margin = new Thickness(46, 4, 10, 8);
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.RenderTransform = new TranslateTransform();
            _cards.Add(card);
            _lines.Children.Add(card);
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
            body.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(new CorvidButton { Content = "Add comment", Variant = ButtonVariant.Primary, Style = SmallButton(), Command = editor.Save });
            buttons.Children.Add(new CorvidButton { Content = "Cancel", Variant = ButtonVariant.Ghost, Style = SmallButton(), Command = editor.Cancel });
            body.Children.Add(buttons);
            var card = Card();
            card.Child = body;
            return card;
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

        private Border Number(string text) => new()
        {
            BorderBrush = ThemeBrushes.Get(_owner, "CorvidLine"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = _mono,
                FontSize = 12,
                Foreground = ThemeBrushes.Get(_owner, "CorvidText2"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

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
