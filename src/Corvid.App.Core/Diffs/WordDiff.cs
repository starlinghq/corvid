namespace Corvid.App.Core.Diffs;

/// <summary>A piece of a diff line, drawn highlighted (the word background) when <see cref="Changed"/>.</summary>
public sealed record DiffSegment(string Text, bool Changed);

/// <summary>
/// Word-level highlights for a removed line and the added line paired with it (docs/plan/ui.md §4.3, §4.7). Lines are cut
/// into words, runs of white space and single punctuation marks; the longest common subsequence of those tokens is kept
/// plain and the rest is highlighted. Two rules keep the result readable: when less than half of the two lines is shared,
/// everything after the indentation is highlighted, as a rewritten line reads better whole; and a lone space or punctuation
/// mark left between or beside changes is folded into them, so <c>namespace.</c> to <c>namespace, unless it.</c> marks
/// <c>.</c> and <c>, unless it.</c> rather than a scatter of pieces.
/// </summary>
internal static class WordDiff
{
    // Past this many tokens a side, the pair is highlighted whole: the table below grows with the product of the two.
    private const int MaxTokens = 400;
    private const double MinShared = 0.5;

    public static (IReadOnlyList<DiffSegment> Old, IReadOnlyList<DiffSegment> New) Compute(string oldLine, string newLine)
    {
        var indent = CommonIndent(oldLine, newLine);
        var oldTokens = Tokenize(oldLine, indent);
        var newTokens = Tokenize(newLine, indent);
        if (oldTokens.Count > MaxTokens || newTokens.Count > MaxTokens)
            return Whole(oldLine, newLine, indent);

        var ops = Script(oldTokens, newTokens);
        var shared = ops.Where(op => op.Kind == OpKind.Equal).Sum(op => op.Text.Length);
        var total = oldLine.Length - indent + newLine.Length - indent;
        if (total == 0)
            return ([new DiffSegment(oldLine, false)], [new DiffSegment(newLine, false)]);
        if (2.0 * shared / total < MinShared)
            return Whole(oldLine, newLine, indent);

        Absorb(ops);
        var oldSegments = new List<DiffSegment>();
        var newSegments = new List<DiffSegment>();
        if (indent > 0)
        {
            oldSegments.Add(new DiffSegment(oldLine[..indent], false));
            newSegments.Add(new DiffSegment(newLine[..indent], false));
        }

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OpKind.Equal:
                    Append(oldSegments, op.Text, false);
                    Append(newSegments, op.Text, false);
                    break;
                case OpKind.Delete:
                    Append(oldSegments, op.Text, true);
                    break;
                case OpKind.Insert:
                    Append(newSegments, op.Text, true);
                    break;
            }
        }

        return (oldSegments, newSegments);
    }

    /// <summary>Words (letters, digits, underscores), runs of white space, and each other character on its own.</summary>
    internal static List<string> Tokenize(string line, int start = 0)
    {
        var tokens = new List<string>();
        var i = start;
        while (i < line.Length)
        {
            var c = line[i];
            var j = i + 1;
            if (IsWord(c))
            {
                while (j < line.Length && IsWord(line[j]))
                    j++;
            }
            else if (char.IsWhiteSpace(c))
            {
                while (j < line.Length && char.IsWhiteSpace(line[j]))
                    j++;
            }
            else if (char.IsHighSurrogate(c) && j < line.Length && char.IsLowSurrogate(line[j]))
            {
                j++;
            }

            tokens.Add(line[i..j]);
            i = j;
        }

        return tokens;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int CommonIndent(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i] && char.IsWhiteSpace(a[i]))
            i++;
        return i;
    }

    private static (IReadOnlyList<DiffSegment>, IReadOnlyList<DiffSegment>) Whole(string oldLine, string newLine, int indent) =>
        (Split(oldLine, indent), Split(newLine, indent));

    private static List<DiffSegment> Split(string line, int indent)
    {
        var segments = new List<DiffSegment>();
        if (indent > 0)
            segments.Add(new DiffSegment(line[..indent], false));
        if (line.Length > indent)
            segments.Add(new DiffSegment(line[indent..], true));
        return segments;
    }

    private static void Append(List<DiffSegment> segments, string text, bool changed)
    {
        if (text.Length == 0)
            return;
        if (segments.Count > 0 && segments[^1].Changed == changed)
            segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
        else
            segments.Add(new DiffSegment(text, changed));
    }

    private enum OpKind
    {
        Equal,
        Delete,
        Insert,
    }

    private sealed record Op(OpKind Kind, string Text);

    /// <summary>The edit script from the longest common subsequence; within a change, deletions come before insertions.</summary>
    private static List<Op> Script(List<string> a, List<string> b)
    {
        var n = a.Count;
        var m = b.Count;
        // lengths[i, j]: the LCS length of a[i..] and b[j..].
        var lengths = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var ops = new List<Op>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                Add(ops, OpKind.Equal, a[x]);
                x++;
                y++;
            }
            else if (y >= m || x < n && lengths[x + 1, y] >= lengths[x, y + 1])
            {
                Add(ops, OpKind.Delete, a[x]);
                x++;
            }
            else
            {
                Add(ops, OpKind.Insert, b[y]);
                y++;
            }
        }

        return Normalize(ops);
    }

    private static void Add(List<Op> ops, OpKind kind, string text)
    {
        if (ops.Count > 0 && ops[^1].Kind == kind)
            ops[^1] = ops[^1] with { Text = ops[^1].Text + text };
        else
            ops.Add(new Op(kind, text));
    }

    /// <summary>Orders each run of changes as its deletion then its insertion, merging pieces of one kind.</summary>
    private static List<Op> Normalize(List<Op> ops)
    {
        var result = new List<Op>();
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == OpKind.Equal)
            {
                result.Add(ops[i++]);
                continue;
            }

            var deleted = "";
            var inserted = "";
            while (i < ops.Count && ops[i].Kind != OpKind.Equal)
            {
                if (ops[i].Kind == OpKind.Delete)
                    deleted += ops[i].Text;
                else
                    inserted += ops[i].Text;
                i++;
            }

            if (deleted.Length > 0)
                result.Add(new Op(OpKind.Delete, deleted));
            if (inserted.Length > 0)
                result.Add(new Op(OpKind.Insert, inserted));
        }

        return result;
    }

    /// <summary>Folds short equalities of white space or punctuation that touch a change into it, on both sides.</summary>
    private static void Absorb(List<Op> ops)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < ops.Count; i++)
            {
                if (ops[i].Kind != OpKind.Equal || !Trivial(ops[i].Text))
                    continue;
                var before = i > 0 && ops[i - 1].Kind != OpKind.Equal;
                var after = i + 1 < ops.Count && ops[i + 1].Kind != OpKind.Equal;
                if (!before && !after)
                    continue;
                var text = ops[i].Text;
                ops[i] = new Op(OpKind.Delete, text);
                ops.Insert(i + 1, new Op(OpKind.Insert, text));
                var normalized = Normalize(ops);
                ops.Clear();
                ops.AddRange(normalized);
                changed = true;
                break;
            }
        }
    }

    private static bool Trivial(string text) => text.Length <= 2 && text.All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c));
}
