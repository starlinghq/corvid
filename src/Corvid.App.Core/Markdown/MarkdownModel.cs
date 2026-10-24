namespace Corvid.App.Core.Markdown;

/// <summary>How a run of text is set. A run can be several at once (bold inside a link).</summary>
[Flags]
public enum InlineStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    /// <summary>Inline code: monospace on the code background.</summary>
    Code = 4,
    /// <summary>A link the app built from the model's text; it opens only on a click, through <see cref="MarkdownRun.Url"/>.</summary>
    Link = 8,
    Strikethrough = 16,
    /// <summary>An image in the model's output, drawn as a link to it and never fetched (docs/plan/ui.md §4.2).</summary>
    Image = 32,
}

/// <summary>A run of text with one style: the unit a native text block draws (a <c>Run</c> in a paragraph).</summary>
/// <param name="Url">For <see cref="InlineStyle.Link"/> and <see cref="InlineStyle.Image"/>: an absolute http, https or
/// mailto address. Any other scheme is dropped and the text stays plain, so a link can never run script.</param>
public sealed record MarkdownRun(string Text, InlineStyle Style = InlineStyle.None, string? Url = null)
{
    public bool IsBold => Style.HasFlag(InlineStyle.Bold);

    public bool IsItalic => Style.HasFlag(InlineStyle.Italic);

    public bool IsCode => Style.HasFlag(InlineStyle.Code);

    public bool IsLink => Url is not null;

    public bool IsStrikethrough => Style.HasFlag(InlineStyle.Strikethrough);
}

/// <summary>
/// One block of a markdown document as the transcript draws it (docs/plan/ui.md §4.2, U-5): native text, never a web
/// view, so HTML from a model never executes. The kinds are <see cref="MarkdownParagraph"/>, <see cref="MarkdownHeading"/>,
/// <see cref="MarkdownList"/>, <see cref="MarkdownCode"/>, <see cref="MarkdownQuote"/>, <see cref="MarkdownTable"/> and
/// <see cref="MarkdownRule"/>.
/// </summary>
public abstract record MarkdownBlock;

/// <summary>A paragraph: runs that wrap as one text block. A hard line break is a run whose text is a line feed.</summary>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

/// <summary>A heading, level 1 to 6.</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownRun> Runs) : MarkdownBlock;

/// <summary>A bulleted or numbered list.</summary>
/// <param name="Start">The first number of an ordered list (1 unless the model wrote another).</param>
public sealed record MarkdownList(bool Ordered, int Start, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

/// <summary>One item: its marker as drawn ("1.", "•") and its blocks, which can nest lists, code and quotes.</summary>
public sealed record MarkdownListItem(string Marker, IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>
/// A code block. <paramref name="IsClosed"/> is false while a fence is still streaming: the rest of the answer so far
/// is drawn as code, so a half-written block never flashes as prose.
/// </summary>
/// <param name="Language">The fence's info word (<c>csharp</c>, <c>sh</c>), lower case; null when none was given.</param>
public sealed record MarkdownCode(string? Language, string Code, bool IsClosed = true) : MarkdownBlock;

/// <summary>A block quote, holding blocks.</summary>
public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>A column's alignment in a table.</summary>
public enum ColumnAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>A pipe table: the header cells, then the rows; each cell is a list of runs.</summary>
public sealed record MarkdownTable(
    IReadOnlyList<ColumnAlignment> Alignments,
    IReadOnlyList<IReadOnlyList<MarkdownRun>> Header,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownRun>>> Rows) : MarkdownBlock
{
    public int Columns => Alignments.Count;
}

/// <summary>A thematic break (<c>---</c>).</summary>
public sealed record MarkdownRule : MarkdownBlock;

/// <summary>A parsed document: its blocks in order, and the source it came from (copies use the source, never the drawing).</summary>
public sealed record MarkdownDocument(string Source, IReadOnlyList<MarkdownBlock> Blocks)
{
    public static MarkdownDocument Empty { get; } = new("", []);

    /// <summary>Parses markdown as the model wrote it; see <see cref="MarkdownParser"/>.</summary>
    public static MarkdownDocument Parse(string? source) => MarkdownParser.Parse(source);
}
