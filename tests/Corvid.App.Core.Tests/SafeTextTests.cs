using Corvid.App.Core.Diffs;

namespace Corvid.App.Core.Tests;

public sealed class SafeTextTests
{
    [Theory]
    [InlineData("echo hi\rrm -rf x", "echo hi␍rm -rf x")]
    [InlineData("cat a\u202Etxt.exe", "cat a⟨U+202E⟩txt.exe")]
    [InlineData("rm\u200B -rf", "rm⟨U+200B⟩ -rf")]
    [InlineData("\u001b[2Jclear", "␛[2Jclear")]
    [InlineData("two\nlines\tand a tab", "two\nlines\tand a tab")]
    [InlineData("plain", "plain")]
    public void RevealShowsWhatCleanWouldHide(string text, string expected) =>
        Assert.Equal(expected, SafeText.Reveal(text));

    [Theory]
    [InlineData("rm -rf /tm\u00ADp", "rm -rf /tm⟨U+00AD⟩p")]
    [InlineData("ls \U000E0041x", "ls ⟨U+E0041⟩x")]
    [InlineData("a\u3164b", "a⟨U+3164⟩b")]
    [InlineData("a\u034Fb", "a⟨U+034F⟩b")]
    [InlineData("x\u2062y", "x⟨U+2062⟩y")]
    public void RevealShowsFormatCharactersFillersAndTagsToo(string text, string expected) =>
        Assert.Equal(expected, SafeText.Reveal(text));

    [Fact]
    public void CleanDropsZeroWidthCharactersAsWellAsBidiOverrides() =>
        Assert.Equal("rm -rf build", SafeText.Clean("rm\u200B -rf\u2060 bu\uFEFFild\u202E"));

    [Fact]
    public void CleanKeepsTheJoinersThatPersianAndEmojiNeed()
    {
        const string persian = "می\u200Cخواهم";
        const string family = "👩\u200D👩\u200D👦";
        Assert.Same(persian, SafeText.Clean(persian));
        Assert.Same(family, SafeText.Clean(family));
        Assert.Equal(family, SafeText.Reveal(family, keepJoiners: true));
        Assert.Contains("⟨U+200D⟩", SafeText.Reveal(family), StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleWithAHiddenCharacterInAPathCannotBeWritten()
    {
        var hidden = new Sessions.RuleRowView(new Sessions.RulePreview("allow", "rm -r") { Paths = ["bu\u00ADild/"] });
        var plain = new Sessions.RuleRowView(new Sessions.RulePreview("allow", "rm -r") { Paths = ["build/"] });

        Assert.True(hidden.HasPathsError);
        Assert.Contains("cannot see", hidden.PathsError, StringComparison.Ordinal);
        hidden.Paths = "build/";
        Assert.True(hidden.HasPathsError);
        Assert.False(plain.HasPathsError);
        Assert.Equal("build/", plain.Paths);
    }

    [Fact]
    public void WordHighlightsStopAtTheirBudgetAndTheLinesKeepTheirColours()
    {
        var line = new string('a', 1000);
        var old = string.Concat(Enumerable.Range(0, 100).Select(i => $"{line} old {i}\n"));
        var @new = string.Concat(Enumerable.Range(0, 100).Select(i => $"{line} new {i}\n"));

        var rows = FileDiff.Compute("long.txt", old, @new).Unified();

        var added = rows.Where(row => row.Kind == DiffRowKind.Added).ToList();
        Assert.Equal(100, added.Count);
        Assert.Contains(added[0].Segments, segment => segment.Changed);
        Assert.DoesNotContain(added[^1].Segments, segment => segment.Changed);
    }

    [Fact]
    public void AProposedEditShowsAHiddenOverrideInItsDiff()
    {
        var diff = FileDiff.Compute("a.cs", "var ok = true;\n", "var ok = true; // \u202Eeslaf\n");

        var added = diff.Unified().Single(row => row.Kind == DiffRowKind.Added);
        Assert.Contains("⟨U+202E⟩", added.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextTooLongToCompareShowsAsAWholeFileChange()
    {
        var old = string.Concat(Enumerable.Range(0, FileDiff.MaxDiffLines / 2 + 1).Select(i => $"old {i}\n"));
        var @new = string.Concat(Enumerable.Range(0, FileDiff.MaxDiffLines / 2 + 1).Select(i => $"new {i}\n"));

        var diff = FileDiff.Compute("big.txt", old, @new);

        Assert.True(diff.IsWholeFile);
        Assert.Equal(FileDiff.MaxDiffLines / 2 + 1, diff.Removed);
        Assert.Equal(FileDiff.MaxDiffLines / 2 + 1, diff.Added);
    }

    [Fact]
    public void AOneLineEditToALongFileStaysAOneLineDiff()
    {
        var lines = Enumerable.Range(0, FileDiff.MaxDiffLines).Select(i => $"line {i}").ToList();
        var old = string.Join('\n', lines) + "\n";
        lines[5000] = "line 5000, changed";
        var @new = string.Join('\n', lines) + "\n";

        var diff = FileDiff.Compute("long.txt", old, @new);

        Assert.False(diff.IsWholeFile);
        Assert.Equal((1, 1), (diff.Added, diff.Removed));
    }

    [Theory]
    [InlineData("build/../etc")]
    [InlineData("build/./x")]
    public void ARulePathThatStepsUpOrAroundIsRefused(string edited)
    {
        var row = new Sessions.RuleRowView(new Sessions.RulePreview("allow", "rm -r") { Paths = ["build/"] }) { Paths = edited };

        Assert.True(row.HasPathsError);
        Assert.Contains(". or ..", row.PathsError, StringComparison.Ordinal);
    }

    [Fact]
    public void ASmallTextIsComparedLineByLine() =>
        Assert.False(FileDiff.Compute("a.txt", "a\nb\n", "a\nc\n").IsWholeFile);
}
