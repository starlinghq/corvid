using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Corvid.App.Core.Markdown;

/// <summary>
/// Markdown from the model into <see cref="MarkdownBlock"/>s, with Markdig (the project's one package). The pipeline is
/// CommonMark with pipe tables, strikethrough and bare-URL links; raw HTML is read as text. Every string is cleaned with
/// <see cref="SafeText"/>, links keep only http, https and mailto addresses, and images become links that are never
/// fetched.
/// <para>
/// Streaming parses the whole answer so far on every chunk, which is what the transcript draws: a list item grows, a
/// table appears once its delimiter row arrives, and a fence that has not closed yet is a <see cref="MarkdownCode"/> with
/// <see cref="MarkdownCode.IsClosed"/> false.
/// </para>
/// </summary>
public static class MarkdownParser
{
    // Nesting deeper than this is drawn flat: a model's runaway quote or list cannot build an unbounded visual tree.
    private const int MaxDepth = 12;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static MarkdownDocument Parse(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return MarkdownDocument.Empty;
        var document = Markdig.Markdown.Parse(source, Pipeline);
        return new MarkdownDocument(source, Blocks(document, 0));
    }

    private static List<MarkdownBlock> Blocks(ContainerBlock container, int depth)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var block in container)
        {
            if (Convert(block, depth) is { } converted)
                blocks.Add(converted);
        }

        return blocks;
    }

    private static MarkdownBlock? Convert(Block block, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return new MarkdownHeading(Math.Clamp(heading.Level, 1, 6), Runs(heading.Inline));
            case ParagraphBlock paragraph:
                return Paragraph(Runs(paragraph.Inline));
            case FencedCodeBlock fenced:
                return new MarkdownCode(Language(fenced.Info), CodeText(fenced), fenced.ClosingFencedCharCount > 0);
            case CodeBlock code:
                return new MarkdownCode(null, CodeText(code));
            case ThematicBreakBlock:
                return new MarkdownRule();
            case LinkReferenceDefinitionGroup or LinkReferenceDefinition or BlankLineBlock:
                return null;
            case Table table:
                return Table(table);
            case ListBlock list when depth < MaxDepth:
                return List(list, depth);
            case QuoteBlock quote when depth < MaxDepth:
                return new MarkdownQuote(Blocks(quote, depth + 1));
            case ContainerBlock container:
                // Past the depth limit, or a container this model has no kind for: its text, flattened.
                return Paragraph([new MarkdownRun(SafeText.Clean(PlainText(container)))]);
            case LeafBlock leaf:
                return leaf.Inline is { } inline ? Paragraph(Runs(inline)) : Paragraph([new MarkdownRun(SafeText.Clean(LeafText(leaf)))]);
            default:
                return null;
        }
    }

    private static MarkdownParagraph? Paragraph(List<MarkdownRun> runs) => runs.Count == 0 ? null : new MarkdownParagraph(runs);

    private static MarkdownList List(ListBlock list, int depth)
    {
        var start = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var first)
            ? first
            : 1;
        var items = new List<MarkdownListItem>();
        var number = start;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item)
                continue;
            var marker = list.IsOrdered
                ? string.Create(CultureInfo.InvariantCulture, $"{number}{(list.OrderedDelimiter == ')' ? ')' : '.')}")
                : "•";
            items.Add(new MarkdownListItem(marker, Blocks(item, depth + 1)));
            number++;
        }

        return new MarkdownList(list.IsOrdered, start, items);
    }

    private static MarkdownTable Table(Table table)
    {
        var alignments = table.ColumnDefinitions.Select(column => column.Alignment switch
        {
            TableColumnAlign.Center => ColumnAlignment.Center,
            TableColumnAlign.Right => ColumnAlignment.Right,
            _ => ColumnAlignment.Left,
        }).ToList();
        IReadOnlyList<IReadOnlyList<MarkdownRun>> header = [];
        var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownRun>>>();
        foreach (var child in table)
        {
            if (child is not TableRow row)
                continue;
            var cells = row.OfType<TableCell>().Select(cell => (IReadOnlyList<MarkdownRun>)CellRuns(cell)).ToList();
            if (row.IsHeader && header.Count == 0)
                header = cells;
            else
                rows.Add(cells);
        }

        // Markdig keeps a trailing definition for a closing pipe; the drawn table has as many columns as its widest row.
        var columns = Math.Max(header.Count, rows.Count == 0 ? 0 : rows.Max(row => row.Count));
        while (alignments.Count < columns)
            alignments.Add(ColumnAlignment.Left);
        if (alignments.Count > columns)
            alignments.RemoveRange(columns, alignments.Count - columns);
        return new MarkdownTable(alignments, header, rows);
    }

    private static List<MarkdownRun> CellRuns(TableCell cell)
    {
        var runs = new List<MarkdownRun>();
        foreach (var block in cell)
        {
            if (block is LeafBlock { Inline: { } inline })
            {
                if (runs.Count > 0)
                    runs.Add(new MarkdownRun(" "));
                runs.AddRange(Runs(inline));
            }
        }

        return Merge(runs);
    }

    private static List<MarkdownRun> Runs(ContainerInline? inline)
    {
        var runs = new List<MarkdownRun>();
        if (inline is not null)
            AddRuns(inline, InlineStyle.None, null, runs, 0);
        return Merge(runs);
    }

    private static void AddRuns(ContainerInline container, InlineStyle style, string? url, List<MarkdownRun> runs, int depth)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Add(runs, literal.Content.ToString(), style, url);
                    break;
                case CodeInline code:
                    Add(runs, code.Content, style | InlineStyle.Code, url);
                    break;
                case LineBreakInline lineBreak:
                    Add(runs, lineBreak.IsHard ? "\n" : " ", style, url);
                    break;
                case HtmlEntityInline entity:
                    Add(runs, entity.Transcoded.ToString(), style, url);
                    break;
                case AutolinkInline autolink:
                {
                    var target = SafeUrl(autolink.IsEmail && !autolink.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                        ? "mailto:" + autolink.Url
                        : autolink.Url);
                    Add(runs, autolink.Url, target is null ? style : style | InlineStyle.Link, target ?? url);
                    break;
                }
                case LinkInline { IsImage: true } image:
                {
                    // Never fetched: the image is a link to it, named by its alt text.
                    var target = SafeUrl(image.Url);
                    var alt = PlainText(image);
                    var text = alt.Length > 0 ? alt : image.Url ?? "image";
                    Add(runs, text, target is null ? style : style | InlineStyle.Image | InlineStyle.Link, target ?? url);
                    break;
                }
                case LinkInline link:
                {
                    var target = SafeUrl(link.Url);
                    var linkStyle = target is null ? style : style | InlineStyle.Link;
                    if (link.FirstChild is null)
                        Add(runs, link.Url ?? "", linkStyle, target ?? url);
                    else if (depth < MaxDepth)
                        AddRuns(link, linkStyle, target ?? url, runs, depth + 1);
                    else
                        Add(runs, PlainText(link), linkStyle, target ?? url);
                    break;
                }
                case EmphasisInline emphasis:
                {
                    var added = emphasis.DelimiterChar switch
                    {
                        '~' => InlineStyle.Strikethrough,
                        _ when emphasis.DelimiterCount >= 2 => InlineStyle.Bold,
                        _ => InlineStyle.Italic,
                    };
                    if (depth < MaxDepth)
                        AddRuns(emphasis, style | added, url, runs, depth + 1);
                    else
                        Add(runs, PlainText(emphasis), style | added, url);
                    break;
                }
                case DelimiterInline delimiter:
                    // An emphasis or link that has not closed (often mid-stream): its delimiter as written, then its content.
                    Add(runs, delimiter.ToLiteral(), style, url);
                    if (depth < MaxDepth)
                        AddRuns(delimiter, style, url, runs, depth + 1);
                    break;
                case HtmlInline html:
                    Add(runs, html.Tag, style, url);
                    break;
                case ContainerInline nested when depth < MaxDepth:
                    AddRuns(nested, style, url, runs, depth + 1);
                    break;
                default:
                    Add(runs, inline.ToString() ?? "", style, url);
                    break;
            }
        }
    }

    private static void Add(List<MarkdownRun> runs, string text, InlineStyle style, string? url)
    {
        text = SafeText.Clean(text);
        if (text.Length > 0)
            runs.Add(new MarkdownRun(text, style, url));
    }

    /// <summary>Joins neighbouring runs of one style, so a paragraph is as few runs as it can be.</summary>
    private static List<MarkdownRun> Merge(List<MarkdownRun> runs)
    {
        var merged = new List<MarkdownRun>(runs.Count);
        foreach (var run in runs)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Style == run.Style && last.Url == run.Url)
                merged[^1] = last with { Text = last.Text + run.Text };
            else
                merged.Add(run);
        }

        // A paragraph that ends in a soft break while streaming would draw a trailing space; drop it.
        if (merged.Count > 0 && merged[^1].Text.EndsWith(' ') && merged[^1].Style == InlineStyle.None)
        {
            var trimmed = merged[^1].Text.TrimEnd(' ');
            if (trimmed.Length == 0)
                merged.RemoveAt(merged.Count - 1);
            else
                merged[^1] = merged[^1] with { Text = trimmed };
        }

        return merged;
    }

    /// <summary>An address the app will open on a click, or null: http, https and mailto only, absolute.</summary>
    internal static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;
        return uri.Scheme is "http" or "https" or "mailto" ? uri.OriginalString : null;
    }

    private static string? Language(string? info)
    {
        var word = info?.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(word) ? null : SafeText.Clean(word).ToLowerInvariant();
    }

    private static string CodeText(LeafBlock code)
    {
        var text = new StringBuilder();
        var lines = code.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                text.Append('\n');
            text.Append(lines.Lines[i].Slice.ToString());
        }

        // A streaming fence's last line can be an empty line still being written.
        return SafeText.Clean(text.ToString().TrimEnd('\n'));
    }

    private static string LeafText(LeafBlock leaf) => CodeText(leaf);

    private static string PlainText(MarkdownObject node)
    {
        var text = new StringBuilder();
        Collect(node, text, 0);
        return text.ToString().Trim();

        static void Collect(MarkdownObject node, StringBuilder text, int depth)
        {
            if (depth > MaxDepth * 4)
                return;
            switch (node)
            {
                case LiteralInline literal:
                    text.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case LineBreakInline:
                    text.Append(' ');
                    break;
                case ContainerInline container:
                    foreach (var child in container)
                        Collect(child, text, depth + 1);
                    break;
                case LeafBlock leaf when leaf.Inline is { } inline:
                    Collect(inline, text, depth + 1);
                    text.Append(' ');
                    break;
                case LeafBlock leaf:
                    text.Append(CodeText(leaf)).Append(' ');
                    break;
                case ContainerBlock block:
                    foreach (var child in block)
                        Collect(child, text, depth + 1);
                    break;
            }
        }
    }
}
