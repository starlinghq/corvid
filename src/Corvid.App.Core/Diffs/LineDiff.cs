namespace Corvid.App.Core.Diffs;

/// <summary>
/// Which lines of two sequences changed: Myers' O(ND) difference algorithm in its linear-space form (the middle snake,
/// "An O(ND) Difference Algorithm and Its Variations", 1986, §4b). A file of a few thousand lines with a small change
/// costs little, and a completely rewritten one costs memory in proportion to its length, never its square.
/// </summary>
internal static class LineDiff
{
    /// <summary>
    /// Marks the elements of <paramref name="a"/> that are not in a longest common subsequence with
    /// <paramref name="b"/> (removed) and those of <paramref name="b"/> that are not (added).
    /// </summary>
    public static (bool[] Removed, bool[] Added) Compute(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var removed = new bool[a.Count];
        var added = new bool[b.Count];
        var max = a.Count + b.Count + 1;
        var down = new int[2 * max + 2];
        var up = new int[2 * max + 2];
        Lcs(a, 0, a.Count, b, 0, b.Count, removed, added, down, up, max);
        return (removed, added);
    }

    /// <summary>Numbers equal strings alike, so the algorithm compares integers.</summary>
    public static (int[] A, int[] B) Number(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int Id(string line)
        {
            if (!ids.TryGetValue(line, out var id))
            {
                id = ids.Count;
                ids[line] = id;
            }

            return id;
        }

        return ([.. a.Select(Id)], [.. b.Select(Id)]);
    }

    private static void Lcs(IReadOnlyList<int> a, int lowerA, int upperA, IReadOnlyList<int> b, int lowerB, int upperB,
        bool[] removed, bool[] added, int[] down, int[] up, int max)
    {
        // Equal lines at the start and the end need no search.
        while (lowerA < upperA && lowerB < upperB && a[lowerA] == b[lowerB])
        {
            lowerA++;
            lowerB++;
        }

        while (lowerA < upperA && lowerB < upperB && a[upperA - 1] == b[upperB - 1])
        {
            upperA--;
            upperB--;
        }

        if (lowerA == upperA)
        {
            while (lowerB < upperB)
                added[lowerB++] = true;
        }
        else if (lowerB == upperB)
        {
            while (lowerA < upperA)
                removed[lowerA++] = true;
        }
        else
        {
            var (x, y) = MiddleSnake(a, lowerA, upperA, b, lowerB, upperB, down, up, max);
            if (x == lowerA && y == lowerB || x == upperA && y == upperB)
            {
                // A split that leaves the whole range on one side would never finish; mark it all changed instead, which is
                // a correct difference if not the shortest. The search does not return one, but a diff must never hang.
                Array.Fill(removed, true, lowerA, upperA - lowerA);
                Array.Fill(added, true, lowerB, upperB - lowerB);
                return;
            }

            Lcs(a, lowerA, x, b, lowerB, y, removed, added, down, up, max);
            Lcs(a, x, upperA, b, y, upperB, removed, added, down, up, max);
        }
    }

    /// <summary>
    /// The point where the furthest-reaching forward and reverse paths of the same edit distance meet; the halves on
    /// either side of it are solved on their own.
    /// </summary>
    private static (int X, int Y) MiddleSnake(IReadOnlyList<int> a, int lowerA, int upperA, IReadOnlyList<int> b, int lowerB,
        int upperB, int[] down, int[] up, int max)
    {
        var downK = lowerA - lowerB;
        var upK = upperA - upperB;
        var delta = upperA - lowerA - (upperB - lowerB);
        var oddDelta = (delta & 1) != 0;
        var downOffset = max - downK;
        var upOffset = max - upK;
        var maxD = (upperA - lowerA + upperB - lowerB) / 2 + 1;

        down[downOffset + downK + 1] = lowerA;
        up[upOffset + upK - 1] = upperA;

        for (var d = 0; d <= maxD; d++)
        {
            // The forward path, one more edit.
            for (var k = downK - d; k <= downK + d; k += 2)
            {
                int x;
                if (k == downK - d)
                {
                    x = down[downOffset + k + 1];
                }
                else
                {
                    x = down[downOffset + k - 1] + 1;
                    if (k < downK + d && down[downOffset + k + 1] >= x)
                        x = down[downOffset + k + 1];
                }

                var y = x - k;
                while (x < upperA && y < upperB && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                down[downOffset + k] = x;
                if (oddDelta && upK - d < k && k < upK + d && up[upOffset + k] <= down[downOffset + k])
                    return (down[downOffset + k], down[downOffset + k] - k);
            }

            // The reverse path, one more edit.
            for (var k = upK - d; k <= upK + d; k += 2)
            {
                int x;
                if (k == upK + d)
                {
                    x = up[upOffset + k - 1];
                }
                else
                {
                    x = up[upOffset + k + 1] - 1;
                    if (k > upK - d && up[upOffset + k - 1] < x)
                        x = up[upOffset + k - 1];
                }

                var y = x - k;
                while (x > lowerA && y > lowerB && a[x - 1] == b[y - 1])
                {
                    x--;
                    y--;
                }

                up[upOffset + k] = x;
                if (!oddDelta && downK - d <= k && k <= downK + d && up[upOffset + k] <= down[downOffset + k])
                    return (down[downOffset + k], down[downOffset + k] - k);
            }
        }

        throw new InvalidOperationException("The paths of the difference search did not meet.");
    }
}
