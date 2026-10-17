namespace Corvid.Vault;

/// <summary>
/// The entropy floor applied at <c>set</c>. A guessable value makes the scrub service an oracle, and a password like
/// <c>hunter2</c> belongs in a password manager, not an agent's toolbelt.
/// </summary>
internal static class Entropy
{
    public const int MinimumBytes = 8;
    public const double MinimumBits = 40;

    /// <summary>Length times the Shannon entropy of the byte distribution: a rough upper bound, not a guarantee.</summary>
    public static double EstimateBits(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return 0;
        }

        Span<int> counts = stackalloc int[256];
        foreach (byte b in value)
        {
            counts[b]++;
        }

        double perByte = 0;
        foreach (int count in counts)
        {
            if (count > 0)
            {
                double p = (double)count / value.Length;
                perByte -= p * Math.Log2(p);
            }
        }

        return perByte * value.Length;
    }

    public static string? Problem(ReadOnlySpan<byte> value)
    {
        if (value.Length < MinimumBytes)
        {
            return $"is {value.Length} bytes; Vault needs at least {MinimumBytes}.";
        }

        double bits = EstimateBits(value);
        return bits < MinimumBits ? $"looks guessable (about {bits:F0} bits; Vault needs {MinimumBits:F0})." : null;
    }
}
