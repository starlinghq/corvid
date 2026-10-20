using Microsoft.Data.Sqlite;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

/// <summary>Plan §10.4: core blocks and flagged facts stay on the machine.</summary>
public sealed class LocalOnlyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Core_blocks_are_local_only_by_default()
    {
        using var store = new TempStore();

        Assert.All(CoreBlockNames.All, name => Assert.True(store.Blocks.Get(name).LocalOnly));
        store.Blocks.Set("agent", "Use British spelling.");
        Assert.Equal("", store.Blocks.Snapshot().Render(includeLocalOnly: false));

        store.Blocks.SetLocalOnly("agent", false);
        Assert.Equal("[agent]\nUse British spelling.\n", store.Blocks.Snapshot().Render(includeLocalOnly: false));
    }

    [Fact]
    public void A_version_1_store_is_migrated_with_every_block_local_only()
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
                command.CommandText = MemoryDatabase.Schema + """
                    INSERT INTO core_blocks (name, content, budget_tokens, local_only, revision, updated_at) VALUES ('user', 'Lives in Leeds.', 350, 0, 1, 0);
                    INSERT INTO facts (fact_id, text, valid_from, recorded_at) VALUES ('fact:old', 'Sam likes tea', 0, 0);
                    """;
                command.ExecuteNonQuery();
            }

            var database = new MemoryDatabase(path);
            var blocks = new CoreBlockStore(database);
            var facts = new FactGraph(database);

            Assert.True(blocks.Get("user").LocalOnly);
            Assert.Equal("Lives in Leeds.", blocks.Get("user").Content);
            Assert.False(facts.Get("fact:old")!.LocalOnly);
            Assert.True(facts.MarkLocalOnly("fact:old").LocalOnly);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_local_only_fact_stays_local_only_through_supersession_and_the_flag_never_clears()
    {
        using var store = new TempStore();
        var fact = store.Facts.Add(new FactInput("Sam's salary is 90k", [Person("Sam")], LocalOnly: true));
        var plain = store.Facts.Add(new FactInput("Sam likes tea", [Person("Sam")]));

        Assert.True(fact.LocalOnly);
        Assert.False(plain.LocalOnly);
        var (_, replaced) = store.Facts.Supersede(fact.FactId, new FactInput("Sam's salary is 95k", [Person("Sam")]));
        Assert.True(replaced.LocalOnly);

        Assert.True(store.Facts.MarkLocalOnly(plain.FactId).LocalOnly);
        store.Facts.Invalidate(plain.FactId);
        using var connection = store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE facts SET local_only=0;";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Throws<KeyNotFoundException>(() => store.Facts.MarkLocalOnly("fact:missing"));
    }

    [Fact]
    public async Task Retrieved_items_carry_the_flag_so_a_cloud_prompt_can_leave_them_out()
    {
        using var store = new TempStore();
        store.Facts.Add(new FactInput("the dentist is Dr Patel", LocalOnly: true));
        store.Facts.Add(new FactInput("the dentist is on Main Street"));

        var result = await store.Retriever().RetrieveAsync(new RetrievalRequest("dentist", 1_000), Ct);

        Assert.Equal(["the dentist is Dr Patel"], result.Items.Where(item => item.LocalOnly).Select(item => item.Text));
        Assert.Equal(["the dentist is on Main Street"], result.Items.Where(item => !item.LocalOnly).Select(item => item.Text));
    }
}
