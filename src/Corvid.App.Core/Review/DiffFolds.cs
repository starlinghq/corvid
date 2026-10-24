namespace Corvid.App.Core.Review;

/// <summary>The diff rows Review and Rewind's preview draw: a file's change from its first hunk to its last.</summary>
internal static class DiffFolds
{
    /// <summary>
    /// Review.png starts at the first hunk and ends at the last: the folds before the first and after the last are left
    /// out (the line numbers say where the hunk is); folds between hunks stay.
    /// </summary>
    public static IReadOnlyList<T> Trim<T>(IReadOnlyList<T> rows, Func<T, bool> isFold)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(isFold);
        var start = 0;
        var end = rows.Count;
        while (start < end && isFold(rows[start]))
            start++;
        while (end > start && isFold(rows[end - 1]))
            end--;
        return start == 0 && end == rows.Count ? rows : rows.Skip(start).Take(end - start).ToList();
    }
}
