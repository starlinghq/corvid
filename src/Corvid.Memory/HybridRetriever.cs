using System.Collections.Immutable;
using Corvid.Abstractions.Tokens;
using Corvid.Fabric;

namespace Corvid.Memory;

public enum MemoryItemKind
{
    Episode,
    Fact,
}

/// <summary>Which ranked lists an item came from.</summary>
[Flags]
public enum RetrievalSources
{
    None = 0,
    Bm25 = 1,
    Vector = 2,
    Graph = 4,
}

/// <param name="Query">The text to search for, usually the user message.</param>
/// <param name="TokenBudget">The most tokens the returned snippets may use together (the generator's budget, plan §9.3).</param>
/// <param name="Entities">The turn's entities; graph expansion starts from those already in memory.</param>
/// <param name="Kinds">Which items to return; null returns episodes and facts.</param>
/// <param name="SessionId">Limits episodes to one session; facts are global.</param>
public sealed record RetrievalRequest(
    string Query,
    int TokenBudget,
    ImmutableArray<EntityRef> Entities = default,
    ImmutableArray<MemoryItemKind>? Kinds = null,
    string? SessionId = null);

/// <param name="Score">The fused score after the recency boost; items are ordered by it.</param>
/// <param name="Hops">Graph distance from a turn entity, when the graph found it.</param>
/// <param name="Fact">The fact, for fact items.</param>
/// <param name="Episode">The episode, for episode items.</param>
public sealed record RetrievedItem(
    string ItemId,
    MemoryItemKind Kind,
    string Text,
    int Tokens,
    double Score,
    RetrievalSources Sources,
    DateTimeOffset RecordedAt,
    int? Hops,
    Fact? Fact,
    Episode? Episode)
{
    /// <summary>A local-only fact: never put in a prompt for a model whose privacy class is cloud (plan §10.4).</summary>
    public bool LocalOnly => Fact?.LocalOnly == true;
}

/// <param name="Embedder">The embedder whose index was searched, or null when retrieval used FTS and the graph only.</param>
/// <param name="Dropped">Candidates left out because they did not fit the token budget.</param>
public sealed record RetrievalResult(ImmutableArray<RetrievedItem> Items, int Tokens, int Dropped, string? Embedder);

/// <param name="Candidates">How many items each ranked list contributes before fusion.</param>
/// <param name="RrfK">The reciprocal rank fusion constant: score = Σ 1/(k + rank).</param>
/// <param name="RecencyWeight">The fused score is multiplied by 1 + weight · 0.5^(age / half-life).</param>
/// <param name="MaxItemTokens">A single snippet is cut to this many tokens, so one long tool result cannot take the budget.</param>
public sealed record RetrievalOptions(
    int Candidates = 20,
    int RrfK = 60,
    double RecencyWeight = 0.5,
    TimeSpan? RecencyHalfLife = null,
    int MaxItemTokens = 200)
{
    public static RetrievalOptions Default { get; } = new();

    public TimeSpan HalfLife => RecencyHalfLife ?? TimeSpan.FromDays(30);

    internal RetrievalOptions Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(RrfK);
        ArgumentOutOfRangeException.ThrowIfNegative(RecencyWeight);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HalfLife, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxItemTokens);
        return this;
    }
}

/// <summary>
/// Hybrid retrieval (plan §9.3): BM25 over FTS5 and vector search, merged by reciprocal rank fusion with a third list from
/// one-hop graph expansion around the turn's entities, then boosted for recency and capped by a token budget. Facts are
/// current ones only. Kev reranking is not built.
/// </summary>
public sealed class HybridRetriever
{
    private readonly EpisodicIndex _episodes;
    private readonly FactGraph _facts;
    private readonly MemoryVectors? _vectors;
    private readonly ITokenCounter _counter;
    private readonly RetrievalOptions _options;
    private readonly TimeProvider _time;

    /// <param name="vectors">Null for FTS and graph retrieval only.</param>
    public HybridRetriever(EpisodicIndex episodes, FactGraph facts, MemoryVectors? vectors = null, ITokenCounter? counter = null,
        RetrievalOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(facts);
        _episodes = episodes;
        _facts = facts;
        _vectors = vectors;
        _counter = counter ?? CharsPerTokenCounter.Instance;
        _options = (options ?? RetrievalOptions.Default).Validate();
        _time = time ?? TimeProvider.System;
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegative(request.TokenBudget);
        var kinds = request.Kinds ?? [MemoryItemKind.Episode, MemoryItemKind.Fact];
        bool wantEpisodes = kinds.Contains(MemoryItemKind.Episode), wantFacts = kinds.Contains(MemoryItemKind.Fact);
        var n = _options.Candidates;

        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        Candidate Of(string itemId) => candidates.TryGetValue(itemId, out var found) ? found : candidates[itemId] = new Candidate(itemId);

        // BM25: one list per table, interleaved by rank so neither table's scores need to be comparable with the other's.
        var bm25 = new List<string>();
        var episodeHits = wantEpisodes ? _episodes.Search(request.Query, n, request.SessionId) : [];
        var factHits = wantFacts ? _facts.Search(request.Query, n) : [];
        for (var i = 0; i < Math.Max(episodeHits.Count, factHits.Count); i++)
        {
            if (i < factHits.Count)
            {
                Of(factHits[i].Fact.FactId).Fact = factHits[i].Fact;
                bm25.Add(factHits[i].Fact.FactId);
            }
            if (i < episodeHits.Count)
            {
                Of(episodeHits[i].Episode.ItemId).Episode = episodeHits[i].Episode;
                bm25.Add(episodeHits[i].Episode.ItemId);
            }
        }
        Fuse(bm25, RetrievalSources.Bm25, Of);

        // Vectors: over-fetch, because invalidated facts and other sessions' episodes are only known after loading.
        var embedder = _vectors?.RetrievalEmbedder?.Id;
        if (_vectors is not null && embedder is not null)
        {
            IReadOnlyList<VectorHit> hits;
            try
            {
                hits = await _vectors.SearchAsync(request.Query, n * 3,
                    id => FactGraph.IsFactItem(id) ? wantFacts : wantEpisodes, cancellationToken);
            }
            // A failed embedder leaves BM25 and the graph; the result reports that no index was searched.
            catch (Exception exception) when (exception is not OperationCanceledException and not ArgumentException)
            {
                hits = [];
                embedder = null;
            }
            var vector = new List<string>();
            var facts = _facts.Get(hits.Select(hit => hit.ItemId).Where(FactGraph.IsFactItem)).ToDictionary(fact => fact.FactId);
            var episodes = _episodes.Get(hits.Select(hit => hit.ItemId).Where(id => !FactGraph.IsFactItem(id))).ToDictionary(e => e.ItemId);
            foreach (var hit in hits)
            {
                if (vector.Count == n)
                    break;
                if (facts.TryGetValue(hit.ItemId, out var fact) && fact.IsCurrent)
                    Of(hit.ItemId).Fact = fact;
                else if (episodes.TryGetValue(hit.ItemId, out var episode) && (request.SessionId is null || episode.SessionId == request.SessionId))
                    Of(hit.ItemId).Episode = episode;
                else
                    continue;
                vector.Add(hit.ItemId);
            }
            Fuse(vector, RetrievalSources.Vector, Of);
        }

        // Graph: one hop around the turn's entities that memory already knows.
        if (wantFacts && !request.Entities.IsDefaultOrEmpty)
        {
            var seeds = _facts.FindEntities(request.Entities).Select(entity => entity.EntityId);
            var graph = _facts.Expand(seeds, n);
            foreach (var hit in graph)
            {
                var candidate = Of(hit.Fact.FactId);
                candidate.Fact = hit.Fact;
                candidate.Hops = Math.Min(candidate.Hops ?? int.MaxValue, hit.Hops);
            }
            Fuse([.. graph.Select(hit => hit.Fact.FactId)], RetrievalSources.Graph, Of);
        }

        var now = _time.GetUtcNow();
        var ranked = candidates.Values
            .Select(candidate => (Candidate: candidate, Score: candidate.Fused * (1 + _options.RecencyWeight * Recency(candidate.RecordedAt, now))))
            .OrderByDescending(pair => pair.Score).ThenByDescending(pair => pair.Candidate.RecordedAt)
            .ThenBy(pair => pair.Candidate.ItemId, StringComparer.Ordinal);

        // Greedy fill: an item that does not fit is skipped, and smaller ones after it may still fit.
        var items = ImmutableArray.CreateBuilder<RetrievedItem>();
        int used = 0, dropped = 0;
        foreach (var (candidate, score) in ranked)
        {
            var text = TextBudget.Truncate(_counter, candidate.Text, _options.MaxItemTokens);
            var tokens = _counter.CountTokens(text);
            if (text.Length == 0 || used + tokens > request.TokenBudget)
            {
                dropped++;
                continue;
            }
            used += tokens;
            items.Add(new RetrievedItem(candidate.ItemId, candidate.Fact is null ? MemoryItemKind.Episode : MemoryItemKind.Fact, text,
                tokens, score, candidate.Sources, candidate.RecordedAt, candidate.Hops, candidate.Fact, candidate.Episode));
        }
        return new RetrievalResult(items.ToImmutable(), used, dropped, embedder);
    }

    private void Fuse(IReadOnlyList<string> ranked, RetrievalSources source, Func<string, Candidate> of)
    {
        for (var i = 0; i < ranked.Count; i++)
        {
            var candidate = of(ranked[i]);
            candidate.Fused += 1.0 / (_options.RrfK + i + 1);
            candidate.Sources |= source;
        }
    }

    private double Recency(DateTimeOffset recordedAt, DateTimeOffset now)
    {
        var age = now - recordedAt;
        return age <= TimeSpan.Zero ? 1 : Math.Pow(0.5, age / _options.HalfLife);
    }

    private sealed class Candidate(string itemId)
    {
        public string ItemId { get; } = itemId;
        public Fact? Fact { get; set; }
        public Episode? Episode { get; set; }
        public double Fused { get; set; }
        public RetrievalSources Sources { get; set; }
        public int? Hops { get; set; }
        public string Text => Fact?.Text ?? Episode!.Text;
        public DateTimeOffset RecordedAt => Fact?.RecordedAt ?? Episode!.RecordedAt;
    }
}
