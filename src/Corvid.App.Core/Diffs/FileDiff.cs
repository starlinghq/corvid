using System.Globalization;

namespace Corvid.App.Core.Diffs;

/// <summary>What a diff row shows: an unchanged line, a removed or an added one, or a fold of unchanged lines.</summary>
public enum DiffRowKind
{
    Context,
    Removed,
    Added,
    /// <summary>Unchanged lines left out between two hunks (<see cref="DiffRow.Skipped"/> of them).</summary>
    Skip,
}

/// <summary>
/// A row of the unified view: one line with its numbers in the old and the new file (a removed line has no new number,
/// an added one no old number), cut into <see cref="DiffSegment"/>s for word highlights; or a <see cref="DiffRowKind.Skip"/>
/// fold.
/// </summary>
public sealed record DiffRow(DiffRowKind Kind, int? OldNumber, int? NewNumber, IReadOnlyList<DiffSegment> Segments, int Skipped = 0)
{
    public string Text => string.Concat(Segments.Select(segment => segment.Text));

    /// <summary>The fold's label: "12 unchanged lines".</summary>
    public string SkipText => Kind == DiffRowKind.Skip
        ? string.Create(CultureInfo.InvariantCulture, $"{Skipped} unchanged line{(Skipped == 1 ? "" : "s")}")
        : "";
}

/// <summary>One side of a side-by-side row: a line's number, kind and segments.</summary>
public sealed record DiffCell(DiffRowKind Kind, int Number, IReadOnlyList<DiffSegment> Segments)
{
    public string Text => string.Concat(Segments.Select(segment => segment.Text));
}

/// <summary>
/// A row of the side-by-side view (Review.png): the old line on the left and the new on the right. A null side is the
/// mockup's empty filler cell, beside lines only the other side has; a <see cref="IsSkip"/> row folds unchanged lines.
/// </summary>
public sealed record SideBySideRow(DiffCell? Left, DiffCell? Right, int Skipped = 0)
{
    public bool IsSkip => Skipped > 0;
}

/// <summary>
/// A file's change as the transcript, the dock and Review draw it (docs/plan/ui.md §4.3, §4.7): the line diff of its old
/// and new text, the counts, and rows for the unified and side-by-side views with word-level highlights on paired lines.
/// Within a change, the n-th removed line is paired with the n-th added one.
/// </summary>
public sealed class FileDiff
{
    /// <summary>Unchanged lines kept around each change in the views (git's default).</summary>
    public const int DefaultContext = 3;

    /// <summary>Old and new lines together beyond which the texts are not compared line by line.</summary>
    public const int MaxDiffLines = 20_000;

    /// <summary>The word highlights' budget for one diff: the sum, over paired lines, of their lengths multiplied.</summary>
    public const long MaxWordWork = 4_000_000;

    private readonly List<Line> _lines;

    private FileDiff(string path, string? oldText, string newText, List<Line> lines, bool wholeFile)
    {
        IsWholeFile = wholeFile;
        Path = path;
        OldText = oldText;
        NewText = newText;
        _lines = lines;
        Added = lines.Count(line => line.Kind == DiffRowKind.Added);
        Removed = lines.Count(line => line.Kind == DiffRowKind.Removed);
    }

    public string Path { get; }

    /// <summary>
    /// The texts were too long to compare line by line (<see cref="MaxDiffLines"/>): every old line shows as removed and
    /// every new one as added.
    /// </summary>
    public bool IsWholeFile { get; }

    /// <summary>The file before; null for a new file.</summary>
    public string? OldText { get; }

    public string NewText { get; }

    public int Added { get; }

    public int Removed { get; }

    public bool IsNewFile => OldText is null;

    public bool HasChanges => Added > 0 || Removed > 0;

    /// <summary>Compares two texts line by line; a missing old text is a new file, every line of it added.</summary>
    public static FileDiff Compute(string path, string? oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(newText);
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        bool[] removed, added;
        var wholeFile = false;
        var wordBudget = MaxWordWork;
        if (oldLines.Count + newLines.Count > MaxDiffLines)
        {
            // The line diff's time grows with the lines times the changes, and it runs when a card is drawn: past the cap
            // the whole old text counts as removed and the whole new text as added.
            removed = Enumerable.Repeat(true, oldLines.Count).ToArray();
            added = Enumerable.Repeat(true, newLines.Count).ToArray();
            wholeFile = true;
        }
        else
        {
            var (a, b) = LineDiff.Number(oldLines, newLines);
            (removed, added) = LineDiff.Compute(a, b);
        }

        var lines = new List<Line>(Math.Max(oldLines.Count, newLines.Count));
        int i = 0, j = 0;
        while (i < oldLines.Count || j < newLines.Count)
        {
            if (i < oldLines.Count && removed[i] || j < newLines.Count && added[j])
            {
                // One change: the removed lines, then the added ones, the n-th of each paired for word highlights.
                var gone = new List<int>();
                var come = new List<int>();
                while (i < oldLines.Count && removed[i])
                    gone.Add(i++);
                while (j < newLines.Count && added[j])
                    come.Add(j++);
                // Past the cap nothing is paired: word highlights would cost as much as the diff saved.
                var pairs = wholeFile ? 0 : Math.Min(gone.Count, come.Count);
                // Word highlights cost each pair's lengths multiplied: past a total budget for the whole diff the lines
                // keep their colours but lose the highlights, so drawing a card stays fast however the edit looks.
                for (var p = 0; p < pairs; p++)
                {
                    wordBudget -= (long)oldLines[gone[p]].Length * newLines[come[p]].Length;
                    if (wordBudget < 0)
                    {
                        pairs = p;
                        break;
                    }
                }

                var oldSegments = new IReadOnlyList<DiffSegment>[gone.Count];
                var newSegments = new IReadOnlyList<DiffSegment>[come.Count];
                for (var p = 0; p < pairs; p++)
                    (oldSegments[p], newSegments[p]) = WordDiff.Compute(oldLines[gone[p]], newLines[come[p]]);
                for (var p = pairs; p < gone.Count; p++)
                    oldSegments[p] = [new DiffSegment(oldLines[gone[p]], false)];
                for (var p = pairs; p < come.Count; p++)
                    newSegments[p] = [new DiffSegment(newLines[come[p]], false)];
                for (var p = 0; p < gone.Count; p++)
                    lines.Add(new Line(DiffRowKind.Removed, gone[p] + 1, null, oldSegments[p]));
                for (var p = 0; p < come.Count; p++)
                    lines.Add(new Line(DiffRowKind.Added, null, come[p] + 1, newSegments[p]));
            }
            else
            {
                lines.Add(new Line(DiffRowKind.Context, i + 1, j + 1, [new DiffSegment(oldLines[i], false)]));
                i++;
                j++;
            }
        }

        return new FileDiff(path, oldText, newText, lines, wholeFile);
    }

    /// <summary>The unified view: each hunk's context, removed and added lines, with folds between hunks.</summary>
    public IReadOnlyList<DiffRow> Unified(int context = DefaultContext)
    {
        var rows = new List<DiffRow>();
        foreach (var (start, end, skippedBefore) in Hunks(context))
        {
            if (skippedBefore > 0)
                rows.Add(new DiffRow(DiffRowKind.Skip, null, null, [], skippedBefore));
            for (var index = start; index < end; index++)
            {
                var line = _lines[index];
                rows.Add(new DiffRow(line.Kind, line.OldNumber, line.NewNumber, line.Segments));
            }
        }

        if (TrailingSkipped(context) is > 0 and var trailing)
            rows.Add(new DiffRow(DiffRowKind.Skip, null, null, [], trailing));
        return rows;
    }

    /// <summary>
    /// The side-by-side view: unchanged lines on both sides; within a change, removed lines on the left beside added
    /// lines on the right, row by row, and an empty cell where one side has fewer.
    /// </summary>
    public IReadOnlyList<SideBySideRow> SideBySide(int context = DefaultContext)
    {
        var rows = new List<SideBySideRow>();
        foreach (var (start, end, skippedBefore) in Hunks(context))
        {
            if (skippedBefore > 0)
                rows.Add(new SideBySideRow(null, null, skippedBefore));
            var index = start;
            while (index < end)
            {
                var line = _lines[index];
                if (line.Kind == DiffRowKind.Context)
                {
                    rows.Add(new SideBySideRow(new DiffCell(DiffRowKind.Context, line.OldNumber!.Value, line.Segments),
                        new DiffCell(DiffRowKind.Context, line.NewNumber!.Value, line.Segments)));
                    index++;
                    continue;
                }

                var left = new List<Line>();
                var right = new List<Line>();
                while (index < end && _lines[index].Kind == DiffRowKind.Removed)
                    left.Add(_lines[index++]);
                while (index < end && _lines[index].Kind == DiffRowKind.Added)
                    right.Add(_lines[index++]);
                for (var r = 0; r < Math.Max(left.Count, right.Count); r++)
                {
                    rows.Add(new SideBySideRow(
                        r < left.Count ? new DiffCell(DiffRowKind.Removed, left[r].OldNumber!.Value, left[r].Segments) : null,
                        r < right.Count ? new DiffCell(DiffRowKind.Added, right[r].NewNumber!.Value, right[r].Segments) : null));
                }
            }
        }

        if (TrailingSkipped(context) is > 0 and var trailing)
            rows.Add(new SideBySideRow(null, null, trailing));
        return rows;
    }

    /// <summary>"+12 −3", as the dock and the ledger print a file's counts.</summary>
    public string CountsText => Counts(Added, Removed);

    public static string Counts(int added, int removed) =>
        string.Create(CultureInfo.InvariantCulture, $"+{added} −{removed}");

    /// <summary>Lines of text, without line terminators; a final line feed does not start another line.</summary>
    public static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];
        var lines = text.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var result = new string[count];
        for (var i = 0; i < count; i++)
            // Revealed, not cleaned: a proposed edit is approved as shown, so a hidden character must show (SafeText.Reveal).
            result[i] = SafeText.Reveal(lines[i].EndsWith('\r') ? lines[i][..^1] : lines[i], keepJoiners: true);
        return result;
    }

    /// <summary>The ranges of lines each hunk shows, and how many unchanged lines are folded before it.</summary>
    private IEnumerable<(int Start, int End, int SkippedBefore)> Hunks(int context)
    {
        context = Math.Max(0, context);
        var changed = Enumerable.Range(0, _lines.Count).Where(index => _lines[index].Kind != DiffRowKind.Context).ToList();
        if (changed.Count == 0)
            yield break;

        var shownUntil = 0;
        var k = 0;
        while (k < changed.Count)
        {
            var start = Math.Max(shownUntil, changed[k] - context);
            var end = Math.Min(_lines.Count, changed[k] + 1 + context);
            k++;
            // Changes whose context touches or overlaps join the same hunk.
            while (k < changed.Count && changed[k] - context <= end)
            {
                end = Math.Min(_lines.Count, changed[k] + 1 + context);
                k++;
            }

            yield return (start, end, start - shownUntil);
            shownUntil = end;
        }
    }

    private int TrailingSkipped(int context)
    {
        var last = _lines.FindLastIndex(line => line.Kind != DiffRowKind.Context);
        return last < 0 ? 0 : Math.Max(0, _lines.Count - (last + 1 + Math.Max(0, context)));
    }

    private sealed record Line(DiffRowKind Kind, int? OldNumber, int? NewNumber, IReadOnlyList<DiffSegment> Segments);
}
