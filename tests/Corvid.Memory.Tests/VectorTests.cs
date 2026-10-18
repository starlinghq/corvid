using Corvid.Abstractions.Events;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

public sealed class VectorIndexTests
{
    [Fact]
    public void Search_returns_nearest_items_of_one_embedder_only()
    {
        using var store = new TempStore();
        var index = store.Index;
        index.Upsert("a", "e1", [1, 0, 0]);
        index.Upsert("b", "e1", [0.7f, 0.7f, 0]);
        index.Upsert("c", "e1", [0, 0, 1]);
        index.Upsert("a", "e2", [0, 1]);

        var hits = index.Search("e1", [2, 0, 0], 2);
        Assert.Equal(["a", "b"], hits.Select(hit => hit.ItemId));
        Assert.Equal(1, hits[0].Similarity, 6);
        Assert.Equal(["b", "c"], index.Search("e1", [1, 0, 0], 5, id => id != "a").Select(hit => hit.ItemId));
        Assert.Equal("a", Assert.Single(index.Search("e2", [0, 3], 5)).ItemId);
        Assert.Equal(3, index.Count("e1"));
        Assert.Equal(new HashSet<string> { "a" }, index.Having("e2", ["a", "b"]));

        index.Upsert("a", "e1", [0, 0, 1]);
        Assert.Equal(3, index.Count("e1"));
        Assert.Equal(["a", "c"], index.Search("e1", [0, 0, 1], 5).Take(2).Select(hit => hit.ItemId).Order());

        index.RemoveEmbedder("e1");
        Assert.Equal(0, index.Count("e1"));
        Assert.Equal(1, index.Count("e2"));
    }

    [Fact]
    public void Vectors_must_match_their_embedder_and_have_a_direction()
    {
        using var store = new TempStore();
        store.Index.Upsert("a", "e1", [1, 0, 0]);
        Assert.Throws<ArgumentException>(() => store.Index.Upsert("b", "e1", [1, 0]));
        Assert.Throws<ArgumentException>(() => store.Index.Search("e1", [1, 0], 3));
        Assert.Throws<ArgumentException>(() => store.Index.Upsert("b", "e1", [0, 0, 0]));
        Assert.Throws<ArgumentException>(() => store.Index.Upsert("b", "e1", [float.NaN, 0, 0]));
    }
}

public sealed class EmbedderSwapTests
{
    private const string Session = "s-1";

    private static void Remember(TempStore store, params string[] messages)
    {
        store.Log.Append(Session, store.Log.GetHead(Session, Ct), [.. messages.Select(m => EventData.Create(new UserMessage(m)))], Ct);
        store.Episodes.CatchUp(store.Log, Session, Ct);
    }

    [Fact]
    public async Task The_first_embedder_on_an_empty_store_is_active_at_once()
    {
        using var store = new TempStore();
        var vectors = store.Vectors(new MemoryEmbedder("old", new KeywordEmbedder("tea")));
        Assert.Equal(new EmbedderState("old", null), vectors.BeginSwap("old"));
        Assert.Equal("old", vectors.RetrievalEmbedder?.Id);

        var fact = store.Facts.Add(new FactInput("Sam likes tea"));
        Assert.True(await vectors.EmbedAsync([(fact.FactId, fact.Text)], Ct));
        Assert.Equal(1, store.Index.Count("old"));
    }

    [Fact]
    public async Task Retrieval_uses_the_old_index_and_fts_until_every_row_has_switched()
    {
        using var store = new TempStore();
        var oldEmbedder = new KeywordEmbedder("tea", "coffee");
        var newEmbedder = new KeywordEmbedder("tea", "coffee", "juice");
        var old = new MemoryEmbedder("old", oldEmbedder);
        var vectors = store.Vectors(old, new MemoryEmbedder("new", newEmbedder));
        vectors.BeginSwap("old");
        Remember(store, "Sam drinks tea", "Ana drinks coffee", "Lee drinks water");
        Assert.Equal(new ReembedProgress("old", 3, 0, true), await vectors.ReembedAsync(cancellationToken: Ct));

        Assert.Equal(new EmbedderState("old", "new"), vectors.BeginSwap("new"));
        var first = await vectors.ReembedAsync(batchSize: 2, Ct);
        Assert.Equal(new ReembedProgress("new", 2, 1, false), first);

        // Mid-swap: queries are embedded by the old embedder and searched against the old index.
        var retriever = store.Retriever(vectors);
        var mid = await retriever.RetrieveAsync(new RetrievalRequest("tea", 500), Ct);
        Assert.Equal("old", mid.Embedder);
        Assert.Equal(["tea"], oldEmbedder.Batches[^1]);
        Assert.Equal(3, store.Index.Count("old"));

        // A write during the swap is embedded with both embedders, so the job does not chase it.
        Remember(store, "Kim drinks juice");
        var episode = store.Episodes.Search("juice").Single().Episode;
        await vectors.EmbedAsync([(episode.ItemId, episode.Text)], Ct);
        Assert.Equal(4, store.Index.Count("old"));

        var last = await vectors.ReembedAsync(batchSize: 10, Ct);
        Assert.Equal(new ReembedProgress("new", 1, 0, true), last);
        Assert.Equal(new EmbedderState("new", null), vectors.State);
        Assert.Equal(0, store.Index.Count("old"));
        Assert.Equal(4, store.Index.Count("new"));
        var after = await retriever.RetrieveAsync(new RetrievalRequest("juice", 500), Ct);
        Assert.Equal("new", after.Embedder);
        Assert.Equal(["juice"], newEmbedder.Batches[^1]);
    }

    [Fact]
    public async Task Without_the_old_embedder_loaded_retrieval_falls_back_to_fts()
    {
        using var store = new TempStore();
        var setup = store.Vectors(new MemoryEmbedder("old", new KeywordEmbedder("tea")));
        setup.BeginSwap("old");
        Remember(store, "Sam drinks tea");
        await setup.ReembedAsync(cancellationToken: Ct);

        var vectors = store.Vectors(new MemoryEmbedder("new", new KeywordEmbedder("tea", "juice")));
        vectors.BeginSwap("new");
        Assert.Null(vectors.RetrievalEmbedder);
        var result = await store.Retriever(vectors).RetrieveAsync(new RetrievalRequest("tea", 500), Ct);
        Assert.Null(result.Embedder);
        Assert.Equal(RetrievalSources.Bm25, Assert.Single(result.Items).Sources);
    }

    [Fact]
    public async Task A_swap_does_not_complete_while_an_item_written_during_the_last_batch_lacks_its_new_vector()
    {
        using var store = new TempStore();
        var newEmbedder = new KeywordEmbedder("tea", "coffee");
        var vectors = store.Vectors(new MemoryEmbedder("old", new KeywordEmbedder("tea")), new MemoryEmbedder("new", newEmbedder));
        vectors.BeginSwap("old");
        var tea = store.Facts.Add(new FactInput("Sam likes tea"));
        Assert.True(await vectors.EmbedAsync([(tea.FactId, tea.Text)], Ct));
        vectors.BeginSwap("new");

        // A write lands while the batch is embedding, and its embedding with the new embedder failed.
        newEmbedder.OnGenerate = () =>
        {
            newEmbedder.OnGenerate = null;
            store.Facts.Add(new FactInput("Ana likes coffee"));
        };
        Assert.Equal(new ReembedProgress("new", 1, 1, false), await vectors.ReembedAsync(batchSize: 10, Ct));
        Assert.Equal(new EmbedderState("old", "new"), vectors.State);
        Assert.Equal(1, store.Index.Count("old"));

        Assert.Equal(new ReembedProgress("new", 1, 0, true), await vectors.ReembedAsync(batchSize: 10, Ct));
        Assert.Equal(new EmbedderState("new", null), vectors.State);
        Assert.Equal(0, store.Index.Count("old"));
        Assert.Equal(2, store.Index.Count("new"));
    }

    [Fact]
    public async Task A_failed_embedding_is_backfilled_by_the_job_and_cancelling_a_swap_drops_its_vectors()
    {
        using var store = new TempStore();
        var flaky = new KeywordEmbedder("tea");
        var vectors = store.Vectors(new MemoryEmbedder("old", flaky), new MemoryEmbedder("new", new KeywordEmbedder("tea", "x")));
        vectors.BeginSwap("old");
        flaky.Fail = true;
        var fact = store.Facts.Add(new FactInput("Sam likes tea"));
        Assert.False(await vectors.EmbedAsync([(fact.FactId, fact.Text)], Ct));
        var failed = await store.Retriever(vectors).RetrieveAsync(new RetrievalRequest("tea", 500), Ct);
        Assert.Null(failed.Embedder);
        Assert.Single(failed.Items);

        flaky.Fail = false;
        Assert.Equal(new ReembedProgress("old", 1, 0, true), await vectors.ReembedAsync(cancellationToken: Ct));
        var second = store.Facts.Add(new FactInput("Ana likes coffee"));
        Assert.True(await vectors.EmbedAsync([(second.FactId, second.Text)], Ct));

        vectors.BeginSwap("new");
        Assert.Equal(new ReembedProgress("new", 1, 1, false), await vectors.ReembedAsync(batchSize: 1, Ct));
        Assert.Equal(new EmbedderState("old", null), vectors.BeginSwap("old"));
        Assert.Equal(0, store.Index.Count("new"));
        Assert.Equal(2, store.Index.Count("old"));
    }
}
