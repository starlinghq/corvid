using Corvid.App.Core.Diffs;

namespace Corvid.App.Core.Tests;

public sealed class SafeTextTests
{
    [Theory]
    [InlineData("echo hi\rrm -rf x", "echo hi␍rm -rf x")]
    [InlineData("cat a‮txt.exe", "cat a⟨U+202E⟩txt.exe")]
    [InlineData("rm​ -rf", "rm⟨U+200B⟩ -rf")]
    [InlineData("\u001b[2Jclear", "␛[2Jclear")]
    [InlineData("two\nlines\tand a tab", "two\nlines\tand a tab")]
    [InlineData("plain", "plain")]
    public void RevealShowsWhatCleanWouldHide(string text, string expected) =>
        Assert.Equal(expected, SafeText.Reveal(text));

    [Fact]
    public void CleanDropsZeroWidthCharactersAsWellAsBidiOverrides() =>
        Assert.Equal("rm -rf build", SafeText.Clean("rm​ -rf⁠ bu﻿ild‮"));

    [Fact]
    public void AProposedEditShowsAHiddenOverrideInItsDiff()
    {
        var diff = FileDiff.Compute("a.cs", "var ok = true;\n", "var ok = true; // ‮eslaf\n");

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
    public void ASmallTextIsComparedLineByLine() =>
        Assert.False(FileDiff.Compute("a.txt", "a\nb\n", "a\nc\n").IsWholeFile);
}
