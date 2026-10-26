using System.Collections.Immutable;

namespace Corvid.Memory;

public enum BlockDiffKind
{
    Kept,
    Removed,
    Added,
}

public sealed record BlockDiffLine(BlockDiffKind Kind, string Text);

/// <summary>
/// A line diff of a block and its rewrite, for the reviewer of a consolidation (the CLI and the app's Review): the lines
/// kept, removed and added, in order, by the longest common run of lines. Blank lines are left out.
/// </summary>
public static class BlockDiff
{
    /// <summary>The cap on lines compared; past it every old line is removed and every new one added.</summary>
    public const int MaxLines = 2_000;

    public static ImmutableArray<BlockDiffLine> Lines(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = Split(before);
        var @new = Split(after);
        if (old.Length > MaxLines || @new.Length > MaxLines)
            return [.. old.Select(line => new BlockDiffLine(BlockDiffKind.Removed, line)),
                .. @new.Select(line => new BlockDiffLine(BlockDiffKind.Added, line))];

        // lengths[i, j]: the longest common run of old[i..] and new[j..].
        var lengths = new int[old.Length + 1, @new.Length + 1];
        for (var i = old.Length - 1; i >= 0; i--)
            for (var j = @new.Length - 1; j >= 0; j--)
                lengths[i, j] = old[i] == @new[j] ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);

        var result = ImmutableArray.CreateBuilder<BlockDiffLine>(old.Length + @new.Length);
        int a = 0, b = 0;
        while (a < old.Length && b < @new.Length)
        {
            if (old[a] == @new[b])
            {
                result.Add(new(BlockDiffKind.Kept, old[a]));
                a++;
                b++;
            }
            else if (lengths[a + 1, b] >= lengths[a, b + 1])
                result.Add(new(BlockDiffKind.Removed, old[a++]));
            else
                result.Add(new(BlockDiffKind.Added, @new[b++]));
        }
        while (a < old.Length)
            result.Add(new(BlockDiffKind.Removed, old[a++]));
        while (b < @new.Length)
            result.Add(new(BlockDiffKind.Added, @new[b++]));
        return result.ToImmutable();
    }

    private static string[] Split(string text) =>
        text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
