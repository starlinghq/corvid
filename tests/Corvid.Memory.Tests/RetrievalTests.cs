using Corvid.Abstractions.Events;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

public sealed class RetrievalTests
{
    private const string Session = "s-1";

    private static async Task<MemoryVectors> Vectors(TempStore store, params string[] keywords)
    {
        var vectors = store.Vectors(new MemoryEmbedder("kw", new KeywordEmbedder(keywords)));
        vectors.BeginSwap("kw");
        await vectors.ReembedAsync(1_000, Ct);
        return vectors;
    }

    [Fact]
    public async Task Reciprocal_rank_fusion_puts_items_found_by_both_lists_first()
    {
        using var store = new TempStore();
        var twice = store.Facts.Add(new FactInput("dentist, dentist"));
        var visit = store.Facts.Add(new FactInput("the dentist visit is booked for the morning"));
        var dental = store.Facts.Add(new FactInput("dental hygiene"));
        // BM25 ranks "twice" first; the vectors rank "visit" first and "twice" last; fusion puts "visit" on top.
        var embedder = new KeywordEmbedder
        {
            Override = text => text switch
            {
                "dentist" or "the dentist visit is booked for the morning" => [1, 0],
                "dental hygiene" => [0.9f, 0.1f],
                _ => [0, 1],
            },
        };
        var vectors = store.Vectors(new MemoryEmbedder("kw", embedder));
        vectors.BeginSwap("kw");
        await vectors.ReembedAsync(1_000, Ct);

        var result = await store.Retriever(vectors, new RetrievalOptions(RecencyWeight: 0)).RetrieveAsync(new RetrievalRequest("dentist", 1_000), Ct);
        Assert.Equal("kw", result.Embedder);
        Assert.Equal([visit.FactId, twice.FactId, dental.FactId], result.Items.Select(item => item.ItemId));
        Assert.Equal(1.0 / 62 + 1.0 / 61, result.Items[0].Score, 9);
        Assert.Equal(1.0 / 61 + 1.0 / 63, result.Items[1].Score, 9);
        Assert.Equal(RetrievalSources.Bm25 | RetrievalSources.Vector, result.Items[0].Sources);
        Assert.Equal(RetrievalSources.Vector, result.Items[2].Sources);
        Assert.All(result.Items, item => Assert.Equal(MemoryItemKind.Fact, item.Kind));
    }

    [Fact]
    public async Task Recency_breaks_a_tie_between_equally_ranked_items()
    {
        using var store = new TempStore();
        // Events are stamped by the log's own clock, so the store's clock moves relative to it.
        var episode = store.Log.Append(Session, 0, [EventData.Create(new UserMessage("the dentist"))], Ct)[0];
        store.Episodes.CatchUp(store.Log, Session, Ct);
        store.Time.Set(episode.RecordedAt.AddDays(90));
        var fresh = store.Facts.Add(new FactInput("the dentist"));

        var result = await store.Retriever().RetrieveAsync(new RetrievalRequest("dentist", 1_000), Ct);
        // Interleaved BM25 puts the fact first anyway; the boost makes the gap larger than rank alone.
        Assert.Equal(fresh.FactId, result.Items[0].ItemId);
        Assert.True(result.Items[0].Score > result.Items[1].Score * 1.3);

        var noBoost = await store.Retriever(options: new RetrievalOptions(RecencyWeight: 0))
            .RetrieveAsync(new RetrievalRequest("dentist", 1_000), Ct);
        Assert.Equal(1.0 / 61, noBoost.Items[0].Score, 9);
        Assert.Equal(1.0 / 62, noBoost.Items[1].Score, 9);
    }

    [Fact]
    public async Task Graph_expansion_finds_facts_the_query_text_does_not_mention()
    {
        using var store = new TempStore();
        store.Facts.Add(new FactInput("Sam works at Acme",
            Relation: new RelationInput(Person("Sam"), "works_at", new EntityRef("organization", "Acme"))));
        var hop1 = store.Facts.Add(new FactInput("Acme moved offices to Leeds", [new EntityRef("organization", "Acme"), Place("Leeds")]));

        var result = await store.Retriever().RetrieveAsync(new RetrievalRequest("what should I get for the birthday", 1_000,
            [Person("Sam"), Place("Nowhere")]), Ct);
        Assert.Equal(2, result.Items.Length);
        var expanded = Assert.Single(result.Items, item => item.ItemId == hop1.FactId);
        Assert.Equal(1, expanded.Hops);
        Assert.Equal(RetrievalSources.Graph, expanded.Sources);
        Assert.Equal(0, result.Items[0].Hops);
    }

    [Fact]
    public async Task The_token_budget_caps_the_result_and_long_items_are_cut()
    {
        using var store = new TempStore();
        store.Log.Append(Session, 0, [EventData.Create(new ToolResult("c-1", JsonValue("tea " + new string('x', 2_000))))], Ct);
        store.Episodes.CatchUp(store.Log, Session, Ct);
        store.Facts.Add(new FactInput("Sam likes green tea"));
        store.Facts.Add(new FactInput("Ana likes black tea with milk"));

        var all = await store.Retriever(options: new RetrievalOptions(MaxItemTokens: 50)).RetrieveAsync(new RetrievalRequest("tea", 1_000), Ct);
        Assert.Equal(3, all.Items.Length);
        var cut = Assert.Single(all.Items, item => item.Kind == MemoryItemKind.Episode);
        Assert.Equal(50, cut.Tokens);
        Assert.EndsWith("…", cut.Text);
        Assert.Equal(all.Items.Sum(item => item.Tokens), all.Tokens);

        var capped = await store.Retriever(options: new RetrievalOptions(MaxItemTokens: 50)).RetrieveAsync(new RetrievalRequest("tea", 12), Ct);
        Assert.True(capped.Tokens <= 12);
        Assert.Equal(3 - capped.Items.Length, capped.Dropped);
        Assert.NotEmpty(capped.Items);
    }

    [Fact]
    public async Task Invalidated_facts_and_unwanted_kinds_are_not_returned()
    {
        using var store = new TempStore();
        var stale = store.Facts.Add(new FactInput("Mark lives in Paris"));
        store.Facts.Invalidate(stale.FactId);
        store.Facts.Add(new FactInput("Mark lives in Leeds"));
        store.Log.Append(Session, 0, [EventData.Create(new UserMessage("Mark lives in Leeds now"))], Ct);
        store.Episodes.CatchUp(store.Log, Session, Ct);
        var vectors = await Vectors(store, "lives");

        var facts = await store.Retriever(vectors).RetrieveAsync(new RetrievalRequest("Mark lives", 1_000, Kinds: [MemoryItemKind.Fact]), Ct);
        Assert.Equal(["Mark lives in Leeds"], facts.Items.Select(item => item.Text));
        var episodes = await store.Retriever(vectors).RetrieveAsync(
            new RetrievalRequest("Mark lives", 1_000, Kinds: [MemoryItemKind.Episode], SessionId: "other"), Ct);
        Assert.Empty(episodes.Items);
    }

    private static System.Text.Json.JsonElement JsonValue(string text) => System.Text.Json.JsonSerializer.SerializeToElement(text);
}
