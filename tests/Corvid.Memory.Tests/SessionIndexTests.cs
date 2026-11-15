using Corvid.Abstractions.Events;
using Microsoft.Data.Sqlite;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

/// <summary>C-044: the session index, one card per session, and its place beside the other memory items' vectors.</summary>
public sealed class SessionIndexTests
{
    [Fact]
    public void A_card_is_replaced_when_its_session_moves_and_full_text_follows_the_new_text()
    {
        using var store = new TempStore();
        var index = new SessionIndex(store.Database, store.Time);

        Assert.True(index.Upsert("cli-a", 4, "Fix the sandbox flake in CI"));
        Assert.True(index.Upsert("cli-b", 2, "Release notes for v0.4"));
        Assert.Equal(new Dictionary<string, long> { ["cli-a"] = 4, ["cli-b"] = 2 }, index.Marks());
        Assert.Equal("cli-a", Assert.Single(index.Search("sandbox")).Card.SessionId);

        // The update trigger keeps the external-content FTS table in step: the old words no longer match.
        Assert.True(index.Upsert("cli-a", 9, "Vault expiry timer, unrelated now"));
        Assert.Empty(index.Search("sandbox"));
        var hit = Assert.Single(index.Search("vault expiry"));
        Assert.Equal(("session:cli-a", 9L), (hit.Card.ItemId, hit.Card.Head));
        Assert.Equal(2, index.Count());

        // The same text at a later head moves the mark and needs no new vector; an older head changes nothing.
        Assert.False(index.Upsert("cli-a", 12, "Vault expiry timer, unrelated now"));
        Assert.False(index.Upsert("cli-a", 3, "stale text"));
        Assert.Equal(12, index.Marks()["cli-a"]);
        Assert.Empty(index.Search("stale"));
    }

    [Fact]
    public void Short_terms_match_by_like_and_ids_are_kept_apart_from_other_items()
    {
        using var store = new TempStore();
        var index = new SessionIndex(store.Database, store.Time);
        index.Upsert("cli-a", 1, "Port the UI to Uno");
        store.Time.Advance(TimeSpan.FromMinutes(1));
        index.Upsert("cli-b", 1, "Tidy the CI cache");

        Assert.Equal(["cli-b", "cli-a"], index.Search("UI CI").Select(hit => hit.Card.SessionId));
        Assert.Equal("cli-a", Assert.Single(index.Search("UI")).Card.SessionId);
        Assert.Equal(0, Assert.Single(index.Search("CI")).Rank);
        Assert.True(SessionIndex.IsSessionItem(SessionIndex.ItemIdOf("cli-a")));
        Assert.False(SessionIndex.IsSessionItem("event:0000"));
        Assert.False(SessionIndex.IsSessionItem("fact:1"));
        Assert.Equal(["cli-a"], index.Get(["cli-a", "cli-z"]).Keys);
    }

    [Fact]
    public async Task Session_cards_are_backfilled_and_survive_an_embedder_swap()
    {
        using var store = new TempStore();
        var index = new SessionIndex(store.Database, store.Time);
        var vectors = store.Vectors(new MemoryEmbedder("old", new KeywordEmbedder("sandbox")), new MemoryEmbedder("new", new KeywordEmbedder("sandbox", "flake")));
        index.Upsert("cli-a", 3, "the sandbox flake");
        // A card alone is an item: the first embedder waits for a swap rather than becoming active over unembedded items.
        Assert.Equal(new EmbedderState(null, "old"), vectors.BeginSwap("old"));
        Assert.Equal(new ReembedProgress("old", 1, 0, true), await vectors.ReembedAsync(cancellationToken: Ct));
        Assert.Equal(new HashSet<string> { "session:cli-a" }, store.Index.Having("old", ["session:cli-a"]));

        vectors.BeginSwap("new");
        var swap = await vectors.ReembedAsync(cancellationToken: Ct);

        Assert.Equal(new ReembedProgress("new", 1, 0, true), swap);
        Assert.Equal(new HashSet<string> { "session:cli-a" }, store.Index.Having("new", ["session:cli-a"]));
        Assert.Equal("session:cli-a", Assert.Single(await vectors.SearchAsync("flake", 5, SessionIndex.IsSessionItem, Ct)).ItemId);
    }

    [Fact]
    public async Task Memory_retrieval_never_returns_or_counts_session_cards()
    {
        using var store = new TempStore();
        var index = new SessionIndex(store.Database, store.Time);
        var vectors = store.Vectors(new MemoryEmbedder("kw", new KeywordEmbedder("walrus")));
        vectors.BeginSwap("kw");
        store.Log.Append("s-1", 0, [EventData.Create(new UserMessage("the walrus plan"))], Ct);
        store.Episodes.CatchUp(store.Log, "s-1", Ct);
        // Many session cards closer to the query than the episode: without the filter they would fill the over-fetch.
        for (var i = 0; i < 80; i++)
            index.Upsert($"cli-{i}", 1, "walrus walrus");
        await vectors.ReembedAsync(1_000, Ct);

        var result = await store.Retriever(vectors, new RetrievalOptions(Candidates: 5)).RetrieveAsync(new RetrievalRequest("walrus", 1_000), Ct);

        var item = Assert.Single(result.Items);
        Assert.Equal(MemoryItemKind.Episode, item.Kind);
        Assert.Equal(RetrievalSources.Bm25 | RetrievalSources.Vector, item.Sources);
    }

    [Fact]
    public void A_version_3_store_is_migrated_to_version_4()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "memory.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = MemoryDatabase.Schema + MemoryDatabase.LocalOnlyMigration + MemoryDatabase.LessonsMigration + """
                    INSERT INTO facts (fact_id, text, valid_from, recorded_at) VALUES ('fact:old', 'Sam likes tea', 0, 0);
                    """;
                command.ExecuteNonQuery();
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(3L, command.ExecuteScalar());
            }

            var database = new MemoryDatabase(path);
            Assert.Equal("Sam likes tea", new FactGraph(database).Get("fact:old")!.Text);
            Assert.True(new SessionIndex(database).Upsert("cli-a", 1, "the sandbox flake"));
            using (var connection = database.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(4L, command.ExecuteScalar());
            }
            // Reopening a version 4 store migrates nothing.
            Assert.Single(new SessionIndex(new MemoryDatabase(path)).Search("sandbox"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_store_newer_than_version_4_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "memory.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=5;";
                command.ExecuteNonQuery();
            }

            Assert.Throws<InvalidDataException>(() => new MemoryDatabase(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
