using Corvid.App.Core.Markdown;

namespace Corvid.App.Core.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void ParagraphsCarryBoldItalicCodeAndLinksAsRuns()
    {
        var document = MarkdownDocument.Parse("Run **dotnet test** on *main* with `--filter Seccomp`, see [the docs](https://docs.example.org/x).");

        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(document.Blocks));
        Assert.Equal(
            ["Run |None|", "dotnet test|Bold|", " on |None|", "main|Italic|", " with |None|", "--filter Seccomp|Code|", ", see |None|",
                "the docs|Link|https://docs.example.org/x", ".|None|"],
            paragraph.Runs.Select(run => $"{run.Text}|{run.Style}|{run.Url}"));
        Assert.True(paragraph.Runs[1].IsBold);
        Assert.True(paragraph.Runs[5].IsCode);
        Assert.True(paragraph.Runs[7].IsLink);
    }

    [Fact]
    public void HeadingsListsQuotesRulesAndTablesAreBlocks()
    {
        var document = MarkdownDocument.Parse("""
            ## Two ways

            1. Skip the control step.
            2. Run it under `unshare`.
               - nested

            > quoted **text**

            ---

            | file | change |
            | :--- | -----: |
            | ci.yml | +3 |
            """);

        Assert.Collection(document.Blocks,
            block => Assert.Equal(2, Assert.IsType<MarkdownHeading>(block).Level),
            block =>
            {
                var list = Assert.IsType<MarkdownList>(block);
                Assert.True(list.Ordered);
                Assert.Equal(["1.", "2."], list.Items.Select(item => item.Marker));
                var second = list.Items[1].Blocks;
                Assert.Equal("Run it under |unshare|.", string.Join("|", Assert.IsType<MarkdownParagraph>(second[0]).Runs.Select(run => run.Text)));
                Assert.Equal("•", Assert.IsType<MarkdownList>(second[1]).Items[0].Marker);
            },
            block => Assert.IsType<MarkdownParagraph>(Assert.Single(Assert.IsType<MarkdownQuote>(block).Blocks)),
            block => Assert.IsType<MarkdownRule>(block),
            block =>
            {
                var table = Assert.IsType<MarkdownTable>(block);
                Assert.Equal([ColumnAlignment.Left, ColumnAlignment.Right], table.Alignments);
                Assert.Equal(["file", "change"], table.Header.Select(cell => cell[0].Text));
                Assert.Equal(["ci.yml", "+3"], table.Rows[0].Select(cell => cell[0].Text));
            });
    }

    [Fact]
    public void FencedCodeKeepsItsLanguageAndAnUnclosedFenceIsStillCode()
    {
        var closed = Assert.IsType<MarkdownCode>(Assert.Single(MarkdownDocument.Parse("```CSharp\nvar x = 1;\n```").Blocks));
        Assert.Equal("csharp", closed.Language);
        Assert.Equal("var x = 1;", closed.Code);
        Assert.True(closed.IsClosed);

        // Mid-stream: the fence has opened and its closing line has not arrived.
        var streaming = MarkdownDocument.Parse("Here:\n\n```yaml\n- name: test\n  retry: 0\n");
        Assert.IsType<MarkdownParagraph>(streaming.Blocks[0]);
        var code = Assert.IsType<MarkdownCode>(streaming.Blocks[1]);
        Assert.False(code.IsClosed);
        Assert.Equal("yaml", code.Language);
        Assert.Equal("- name: test\n  retry: 0", code.Code);
    }

    [Fact]
    public void StreamingReparsesTheGrowingTextIntoTheBlocksSoFar()
    {
        const string Answer = "Two ways through:\n\n1. Skip the control step.\n2. Run the control under ";
        var partial = MarkdownDocument.Parse(Answer);
        var list = Assert.IsType<MarkdownList>(partial.Blocks[1]);
        Assert.Equal("Run the control under", Assert.IsType<MarkdownParagraph>(list.Items[1].Blocks[0]).Runs[0].Text);

        var whole = MarkdownDocument.Parse(Answer + "a user namespace.");
        Assert.Equal("Run the control under a user namespace.",
            Assert.IsType<MarkdownParagraph>(Assert.IsType<MarkdownList>(whole.Blocks[1]).Items[1].Blocks[0]).Runs[0].Text);
        Assert.Equal(Answer + "a user namespace.", whole.Source);
    }

    [Fact]
    public void LinksKeepOnlyWebAndMailAddressesAndImagesAreNeverFetched()
    {
        var document = MarkdownDocument.Parse("[run me](javascript:alert(1)) ![diagram](https://example.org/d.png) <mail@example.org> https://ok.example.org");

        var runs = Assert.IsType<MarkdownParagraph>(Assert.Single(document.Blocks)).Runs;
        Assert.Equal("run me", runs[0].Text.TrimEnd());
        Assert.Null(runs[0].Url);
        var image = runs.Single(run => run.Style.HasFlag(InlineStyle.Image));
        Assert.Equal("diagram", image.Text);
        Assert.Equal("https://example.org/d.png", image.Url);
        Assert.Contains(runs, run => run.Url == "mailto:mail@example.org");
        Assert.Contains(runs, run => run.Url == "https://ok.example.org");
    }

    [Fact]
    public void HtmlIsTextAndControlCharactersAreRemoved()
    {
        var document = MarkdownDocument.Parse("<script>alert(1)</script> and \u001b[31mred\u001b[0m and ‮evil");

        var text = string.Concat(Assert.IsType<MarkdownParagraph>(Assert.Single(document.Blocks)).Runs.Select(run => run.Text));
        Assert.Contains("<script>", text, StringComparison.Ordinal);
        Assert.Contains("red", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('‮', text);
    }

    [Fact]
    public void AnEmptyAnswerHasNoBlocks() => Assert.Empty(MarkdownDocument.Parse("").Blocks);
}
