using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Corvid.Fabric;

/// <summary>A tool or skill the shortlist can offer (plan §5.4).</summary>
/// <param name="Examples">Synthetic requests the item answers. With examples the item is embedded multi-vector; without, by its description.</param>
public sealed record ShortlistItem(string Name, string Description, ImmutableArray<string> Examples = default)
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name) ? Name : throw new ArgumentException("An item needs a name.", nameof(Name));

    public string Description { get; } = Description ?? throw new ArgumentNullException(nameof(Description));

    public ImmutableArray<string> Examples { get; init; } = Examples;

    /// <summary>The text embedded for the item itself, as Kev reads an option: <c>name: description</c>.</summary>
    public string Text => Description.Length == 0 ? Name : $"{Name}: {Description}";
}

/// <param name="Size">How many items to keep (plan §5.3: a shortlist of 3–7).</param>
/// <param name="RecencyWeight">Added to the similarity of an item used most recently, halving every <paramref name="RecencyHalfLife"/> uses since.</param>
/// <param name="RecencyHalfLife">In tool uses: an item used this many calls ago gets half the boost.</param>
public sealed record ShortlistOptions(int Size = 5, double RecencyWeight = 0.1, double RecencyHalfLife = 3)
{
    public static ShortlistOptions Default { get; } = new();

    public ShortlistOptions Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Size);
        ArgumentOutOfRangeException.ThrowIfNegative(RecencyWeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RecencyHalfLife);
        return this;
    }
}

/// <param name="Similarity">The best cosine similarity between the query and any of the item's vectors.</param>
/// <param name="Score">The similarity plus the recency boost; entries are ordered by it.</param>
public sealed record ShortlistEntry(ShortlistItem Item, double Similarity, double Score);

/// <param name="Ranked">False when the embedder failed: the entries are the filtered items by recency, then name, unranked.</param>
public sealed record ShortlistResult(ImmutableArray<ShortlistEntry> Entries, bool Ranked, string? Failure = null)
{
    public ImmutableArray<ShortlistItem> Items => [.. Entries.Select(entry => entry.Item)];
}

/// <summary>
/// The embedding shortlist of plan §5.4. Hard filters run first; each remaining item is embedded once per embedder, as its
/// description plus one vector per example request (multi-vector), and scored by the best similarity to the query, with a
/// boost for items used recently in the session. The top <see cref="ShortlistOptions.Size"/> become the <c>tool</c>
/// question's options and the tools the generator may see.
/// </summary>
/// <remarks>Vectors are cached per embedder instance and item text, so a switch of <c>embedder.text</c> starts a fresh cache.</remarks>
public sealed class ToolShortlist
{
    private readonly ShortlistOptions _options;
    private readonly Lock _gate = new();
    private object? _cachedEmbedder;
    private Dictionary<string, float[]> _cache = new(StringComparer.Ordinal);

    public ToolShortlist(ShortlistOptions? options = null)
    {
        _options = (options ?? ShortlistOptions.Default).Validate();
    }

    public ShortlistOptions Options => _options;

    /// <param name="query">The text to match, usually the user message.</param>
    /// <param name="filter">Hard filters (toolset enabled, platform allowed, availability); an item it rejects is never offered.</param>
    /// <param name="usesAgo">For items used in this session: how many tool calls ago (0 is the latest).</param>
    public async Task<ShortlistResult> RankAsync(string query, IEnumerable<ShortlistItem> items,
        IEmbeddingGenerator<string, Embedding<float>> embedder, Func<ShortlistItem, bool>? filter = null,
        IReadOnlyDictionary<string, int>? usesAgo = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(embedder);
        var candidates = items.Where(item => filter is null || filter(item))
            .DistinctBy(item => item.Name, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0)
            return new ShortlistResult([], Ranked: true);

        Dictionary<string, float[]> cache;
        lock (_gate)
        {
            if (!ReferenceEquals(_cachedEmbedder, embedder))
            {
                _cachedEmbedder = embedder;
                _cache = new(StringComparer.Ordinal);
            }
            cache = _cache;
        }

        var texts = candidates.SelectMany(Texts).Distinct(StringComparer.Ordinal).ToList();
        List<string> missing;
        lock (_gate)
            missing = [.. texts.Where(text => !cache.ContainsKey(text))];

        float[] queryVector;
        try
        {
            // One batch: the query, then every item text not embedded yet.
            var embeddings = await embedder.GenerateAsync([query, .. missing], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (embeddings.Count != missing.Count + 1)
                return Unranked(candidates, usesAgo, "the embedder returned the wrong number of vectors");
            queryVector = embeddings[0].Vector.ToArray();
            lock (_gate)
                for (var i = 0; i < missing.Count; i++)
                    cache[missing[i]] = embeddings[i + 1].Vector.ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unranked(candidates, usesAgo, exception.Message);
        }

        var entries = new List<ShortlistEntry>();
        foreach (var item in candidates)
        {
            double best;
            lock (_gate)
                best = Texts(item).Select(text => Cosine(queryVector, cache[text])).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max();
            if (double.IsNaN(best))
                continue;
            entries.Add(new ShortlistEntry(item, best, best + Boost(item, usesAgo)));
        }
        return new ShortlistResult(
            [.. entries.OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Item.Name, StringComparer.Ordinal).Take(_options.Size)],
            Ranked: true);
    }

    private static IEnumerable<string> Texts(ShortlistItem item) => [item.Text, .. (item.Examples.IsDefault ? [] : item.Examples).Where(example => !string.IsNullOrWhiteSpace(example))];

    private double Boost(ShortlistItem item, IReadOnlyDictionary<string, int>? usesAgo) =>
        usesAgo is not null && usesAgo.TryGetValue(item.Name, out var ago) && ago >= 0
            ? _options.RecencyWeight * Math.Pow(0.5, ago / _options.RecencyHalfLife)
            : 0;

    private ShortlistResult Unranked(List<ShortlistItem> candidates, IReadOnlyDictionary<string, int>? usesAgo, string failure) =>
        new([.. candidates.OrderByDescending(item => Boost(item, usesAgo)).ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(_options.Size).Select(item => new ShortlistEntry(item, double.NaN, Boost(item, usesAgo)))], Ranked: false, failure);

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return double.NaN;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? double.NaN : dot / Math.Sqrt(normA * normB);
    }
}
