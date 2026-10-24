using Corvid.App.Core.Markdown;
using Corvid.App.Infrastructure;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Controls;

/// <summary>
/// An answer's markdown in native text blocks (Main.dc.html <c>.answer</c>; decision U-5): paragraphs at 1.6 line
/// height, lists with their markers, fenced code on the code background, quotes, tables and rules. While the answer
/// streams, an accent caret (▍) follows the last word. Nothing is fetched: images are never in the model.
/// </summary>
public sealed partial class MarkdownView : UserControl
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(MarkdownDocument), typeof(MarkdownView), new PropertyMetadata(null, (d, _) => ((MarkdownView)d).Rebuild()));

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false, (d, _) => ((MarkdownView)d).Rebuild()));

    private const double BodySize = 13.5;
    private const double BodyLine = 21.6;
    private readonly StackPanel _root = new() { Spacing = 8 };
    private TextBlock? _last;

    public MarkdownView()
    {
        Content = _root;
        IsTabStop = false;
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public MarkdownDocument? Document
    {
        get => (MarkdownDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        _last = null;
        foreach (var block in Document?.Blocks ?? [])
            _root.Children.Add(Block(block));
        if (!IsStreaming)
            return;
        if (_last is null)
        {
            _last = Text([]);
            _root.Children.Add(_last);
        }

        _last.Inlines.Add(new Run { Text = "▍", Foreground = ThemeBrushes.Get(this, "CorvidAccent") });
        AutomationProperties.SetName(this, "Answer, still streaming");
    }

    private UIElement Block(MarkdownBlock block) => block switch
    {
        MarkdownParagraph paragraph => Text(paragraph.Runs),
        MarkdownHeading heading => Heading(heading),
        MarkdownList list => List(list),
        MarkdownCode code => Code(code),
        MarkdownQuote quote => Quote(quote),
        MarkdownTable table => Table(table),
        MarkdownRule => new Border { Height = 1, Background = ThemeBrushes.Get(this, "CorvidLine"), Margin = new Thickness(0, 4, 0, 4) },
        _ => Text([new MarkdownRun(block.ToString() ?? "")]),
    };

    private TextBlock Text(IReadOnlyList<MarkdownRun> runs, double size = BodySize, double line = BodyLine)
    {
        var text = new TextBlock
        {
            FontSize = size,
            LineHeight = line,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrushes.Get(this, "CorvidText"),
        };
        RichText.Append(text.Inlines, runs, this);
        _last = text;
        return text;
    }

    private TextBlock Heading(MarkdownHeading heading)
    {
        var size = heading.Level switch { 1 => 17, 2 => 15, _ => BodySize };
        var text = Text(heading.Runs, size, Math.Round(size * 1.45, 2));
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(text, (Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel)Math.Clamp(heading.Level + 1, 1, 9));
        return text;
    }

    private StackPanel List(MarkdownList list)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (var item in list.Items)
        {
            var row = new Grid { ColumnSpacing = 0 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = new TextBlock
            {
                Text = item.Marker,
                FontSize = BodySize,
                LineHeight = BodyLine,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = ThemeBrushes.Get(this, "CorvidText"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var body = new StackPanel { Spacing = 4 };
            foreach (var child in item.Blocks)
                body.Children.Add(Block(child));
            Grid.SetColumn(body, 1);
            row.Children.Add(marker);
            row.Children.Add(body);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Border Code(MarkdownCode code)
    {
        var text = new TextBlock
        {
            Text = code.Code.TrimEnd('\n'),
            FontFamily = (FontFamily)Application.Current.Resources["CorvidMono"],
            FontSize = 12.5,
            LineHeight = 18.75,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Foreground = ThemeBrushes.Get(this, "CorvidText"),
        };
        _last = null;
        return new Border
        {
            Background = ThemeBrushes.Get(this, "CorvidCodeBg"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new ScrollViewer
            {
                Content = text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
            },
        };
    }

    private Border Quote(MarkdownQuote quote)
    {
        var body = new StackPanel { Spacing = 8 };
        foreach (var child in quote.Blocks)
        {
            var element = Block(child);
            if (element is TextBlock text)
                text.Foreground = ThemeBrushes.Get(this, "CorvidText2");
            body.Children.Add(element);
        }

        return new Border
        {
            BorderBrush = ThemeBrushes.Get(this, "CorvidLine"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Child = body,
        };
    }

    private Border Table(MarkdownTable table)
    {
        var grid = new Grid();
        for (var c = 0; c < table.Columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        AddRow(grid, 0, table.Header, table.Alignments, header: true);
        for (var r = 0; r < table.Rows.Count; r++)
            AddRow(grid, r + 1, table.Rows[r], table.Alignments, header: false);
        _last = null;
        return new Border
        {
            BorderBrush = ThemeBrushes.Get(this, "CorvidLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new ScrollViewer
            {
                Content = grid,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
            },
        };
    }

    private void AddRow(Grid grid, int row, IReadOnlyList<IReadOnlyList<MarkdownRun>> cells, IReadOnlyList<ColumnAlignment> alignments, bool header)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < cells.Count && c < alignments.Count; c++)
        {
            var text = Text(cells[c], 12.5, 18.1);
            text.TextAlignment = alignments[c] switch
            {
                ColumnAlignment.Center => TextAlignment.Center,
                ColumnAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
            if (header)
            {
                text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                text.Foreground = ThemeBrushes.Get(this, "CorvidText2");
            }

            var cell = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                BorderBrush = ThemeBrushes.Get(this, "CorvidLine"),
                BorderThickness = new Thickness(0, row == 0 ? 0 : 1, 0, 0),
                Child = text,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }
}
