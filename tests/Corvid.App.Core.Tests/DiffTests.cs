using Corvid.App.Core.Diffs;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

public sealed class DiffTests
{
    [Fact]
    public void TheReviewMockupsFileDiffsAsDrawn()
    {
        var diff = FileDiff.Compute(DemoFiles.SeccompTestsPath, DemoFiles.SeccompTestsBefore, DemoFiles.SeccompTestsAfter);

        Assert.Equal(6, diff.Added);
        Assert.Equal(2, diff.Removed);
        var rows = diff.SideBySide();
        // The hunk starts three lines above the first change, at line 41 as Review.png shows.
        Assert.True(rows[0].IsSkip);
        Assert.Equal(40, rows[0].Skipped);
        Assert.Equal((41, 41), (rows[1].Left!.Number, rows[1].Right!.Number));
        Assert.Equal("    [Fact]", rows[1].Left!.Text);

        var line44 = rows.Single(row => row.Left?.Number == 44);
        Assert.Equal(DiffRowKind.Removed, line44.Left!.Kind);
        Assert.Equal(DiffRowKind.Added, line44.Right!.Kind);
        Assert.Equal(["."], line44.Left.Segments.Where(segment => segment.Changed).Select(segment => segment.Text));
        Assert.Equal([", unless the sysctl forbids it."], line44.Right.Segments.Where(segment => segment.Changed).Select(segment => segment.Text));

        // A rewritten line is highlighted whole after its indentation.
        var line46 = rows.Single(row => row.Left?.Number == 46);
        Assert.Equal(["        ", "Assert.Equal(0, control.ExitCode);"], line46.Left!.Segments.Select(segment => segment.Text));
        Assert.Equal([false, true], line46.Left.Segments.Select(segment => segment.Changed));
        Assert.True(line46.Right!.Segments[^1].Changed);

        // The new helper has no old lines beside it: empty cells on the left.
        var added = rows.Where(row => row.Left is null && row.Right is { Kind: DiffRowKind.Added }).Select(row => row.Right!.Number).ToList();
        Assert.Equal(4, added.Count);
        Assert.Contains(54, added);
        Assert.Contains(55, added);
    }

    [Fact]
    public void UnifiedRowsPutRemovedLinesBeforeAddedOnesAndFoldUnchangedLines()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        var after = before.Replace("line 5\n", "line five\n", StringComparison.Ordinal).Replace("line 15\n", "line 15\nline 15b\n", StringComparison.Ordinal);

        var diff = FileDiff.Compute("a.txt", before, after);
        var rows = diff.Unified();

        Assert.Equal((2, 1), (diff.Added, diff.Removed));
        Assert.Equal("+2 −1", diff.CountsText);
        Assert.Equal(DiffRowKind.Skip, rows[0].Kind);
        Assert.Equal(1, rows[0].Skipped);
        Assert.Equal("1 unchanged line", rows[0].SkipText);
        var five = rows.ToList().FindIndex(row => row.Kind == DiffRowKind.Removed);
        Assert.Equal("line 5", rows[five].Text);
        Assert.Equal((DiffRowKind.Added, "line five"), (rows[five + 1].Kind, rows[five + 1].Text));
        Assert.Equal([(int?)5, null], new[] { rows[five].OldNumber, rows[five].NewNumber });
        // The two changes are far apart: a fold between their hunks, and one after the last.
        Assert.Contains(rows.Skip(five), row => row.Kind == DiffRowKind.Skip && row.Skipped == 4);
        Assert.Equal(DiffRowKind.Skip, rows[^1].Kind);
        Assert.Equal("line 15b", rows.Single(row => row.NewNumber == 16).Text);
    }

    [Fact]
    public void ANewFileIsAllAddedAndIdenticalTextsHaveNoRows()
    {
        var created = FileDiff.Compute("new.md", null, "# Title\n\nbody\n");
        Assert.True(created.IsNewFile);
        Assert.Equal((3, 0), (created.Added, created.Removed));
        Assert.All(created.Unified(), row => Assert.Equal(DiffRowKind.Added, row.Kind));

        var same = FileDiff.Compute("same.md", "a\nb\n", "a\r\nb\r\n");
        Assert.False(same.HasChanges);
        Assert.Empty(same.Unified());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheLineDiffIsAShortestEditScript(int seed)
    {
        var random = new Random(seed);
        for (var round = 0; round < 60; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 18)).Select(_ => random.Next(0, 5)).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 18)).Select(_ => random.Next(0, 5)).ToArray();

            var (removed, added) = LineDiff.Compute(a, b);

            // What is kept is a common subsequence, in order, and the longest one.
            var keptA = a.Where((_, i) => !removed[i]).ToArray();
            var keptB = b.Where((_, i) => !added[i]).ToArray();
            Assert.Equal(keptA, keptB);
            Assert.Equal(Lcs(a, b), keptA.Length);
        }
    }

    [Fact]
    public void ALargeRewriteFinishes()
    {
        var a = Enumerable.Range(0, 4000).Select(i => i).ToArray();
        var b = Enumerable.Range(0, 4000).Select(i => i % 3 == 0 ? -i - 1 : i).ToArray();

        var (removed, added) = LineDiff.Compute(a, b);

        Assert.Equal(1334, removed.Count(flag => flag));
        Assert.Equal(1334, added.Count(flag => flag));
    }

    [Fact]
    public void WordHighlightsFoldStrayPunctuationIntoTheChange()
    {
        // As Review.png marks "namespace." against "namespace, unless …": the closing marks join the change.
        var (old, @new) = WordDiff.Compute("var total = sum(a, b);", "var total = sum(a, b, c);");

        Assert.Equal("var total = sum(a, b);", string.Concat(old.Select(segment => segment.Text)));
        Assert.Equal("var total = sum(a, b, c);", string.Concat(@new.Select(segment => segment.Text)));
        Assert.Equal([");"], old.Where(segment => segment.Changed).Select(segment => segment.Text));
        Assert.Equal([", c);"], @new.Where(segment => segment.Changed).Select(segment => segment.Text));

        // A word changed in the middle keeps the words around it plain.
        var (before, after) = WordDiff.Compute("retry: 2 on the test job", "retry: 0 on the test job");
        Assert.Equal(["2"], before.Where(segment => segment.Changed).Select(segment => segment.Text));
        Assert.Equal(["0"], after.Where(segment => segment.Changed).Select(segment => segment.Text));
    }

    private static int Lcs(int[] a, int[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        }

        return table[0, 0];
    }
}
