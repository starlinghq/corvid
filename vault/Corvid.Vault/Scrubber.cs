using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Corvid.Vault;

/// <summary>
/// Replaces secret values with <c>[secret:name]</c>. Each value is matched raw and in the encodings that tools and APIs
/// commonly produce: base64 (padded and unpadded), base64url, percent-encoding, hex in both cases, JSON-escaped, and a
/// leading run of the raw value (catches truncated echoes). At each position the longest pattern wins.
/// </summary>
/// <remarks>
/// Not caught: a value embedded inside a larger encoded blob (for example base64 of <c>user:token</c> in a Basic
/// auth header), or a value split across separately encoded pieces. Those need the egress path to keep values out of
/// reach in the first place.
/// </remarks>
internal sealed class Scrubber
{
    public const int MinimumPatternBytes = 8;
    public const int PrefixBytes = 12;

    private readonly Dictionary<byte, Pattern[]> _byFirstByte;

    private Scrubber(Dictionary<byte, Pattern[]> byFirstByte) => _byFirstByte = byFirstByte;

    public static Scrubber Empty { get; } = new([]);

    public bool IsEmpty => _byFirstByte.Count == 0;

    public static Scrubber Build(IReadOnlyDictionary<string, StoredSecret> secrets)
    {
        var patterns = new List<Pattern>();
        foreach (var (name, secret) in secrets)
        {
            byte[] replacement = Encoding.UTF8.GetBytes($"[secret:{name}]");
            foreach (byte[] variant in Variants(secret.Value))
            {
                if (variant.Length >= MinimumPatternBytes)
                {
                    patterns.Add(new Pattern(variant, replacement));
                }
            }
        }

        var byFirstByte = patterns
            .DistinctBy(p => Convert.ToHexString(p.Bytes))
            .GroupBy(p => p.Bytes[0])
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Bytes.Length).ToArray());
        return new Scrubber(byFirstByte);
    }

    public (string Text, int Hits) Scrub(string text)
    {
        if (IsEmpty)
        {
            return (text, 0);
        }

        (byte[] bytes, int hits) = Scrub(Encoding.UTF8.GetBytes(text));
        return hits == 0 ? (text, 0) : (Encoding.UTF8.GetString(bytes), hits);
    }

    public (byte[] Bytes, int Hits) Scrub(byte[] input)
    {
        if (IsEmpty || input.Length == 0)
        {
            return (input, 0);
        }

        using var output = new MemoryStream(input.Length);
        int hits = 0;
        int copiedTo = 0;
        int i = 0;
        while (i < input.Length)
        {
            Pattern? match = null;
            if (_byFirstByte.TryGetValue(input[i], out Pattern[]? candidates))
            {
                ReadOnlySpan<byte> rest = input.AsSpan(i);
                foreach (Pattern candidate in candidates)
                {
                    if (rest.StartsWith(candidate.Bytes))
                    {
                        match = candidate;
                        break;
                    }
                }
            }

            if (match is null)
            {
                i++;
                continue;
            }

            output.Write(input, copiedTo, i - copiedTo);
            output.Write(match.Replacement);
            hits++;
            i += match.Bytes.Length;
            copiedTo = i;
        }

        if (hits == 0)
        {
            return (input, 0);
        }

        output.Write(input, copiedTo, input.Length - copiedTo);
        return (output.ToArray(), hits);
    }

    internal static IEnumerable<byte[]> Variants(byte[] value)
    {
        yield return value;
        if (value.Length > PrefixBytes + 4)
        {
            yield return value[..PrefixBytes];
        }

        string base64 = Convert.ToBase64String(value);
        yield return Encoding.ASCII.GetBytes(base64);
        yield return Encoding.ASCII.GetBytes(base64.TrimEnd('='));
        yield return Encoding.ASCII.GetBytes(base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        yield return Encoding.ASCII.GetBytes(Convert.ToHexStringLower(value));
        yield return Encoding.ASCII.GetBytes(Convert.ToHexString(value));

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(value);
        }
        catch (DecoderFallbackException)
        {
            yield break;
        }

        yield return Encoding.ASCII.GetBytes(Uri.EscapeDataString(text));
        yield return Encoding.UTF8.GetBytes(JsonEncodedText.Encode(text).ToString());
        yield return Encoding.UTF8.GetBytes(JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
    }

    private sealed record Pattern(byte[] Bytes, byte[] Replacement);
}
