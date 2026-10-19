using Microsoft.Extensions.AI;

namespace Corvid.Fabric.Tests;

public sealed class ShortlistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ShortlistItem[] Tools =
    [
        new("cron_update", "change a scheduled reminder"),
        new("message_send", "send a message to a person"),
        new("weather", "look up the forecast"),
        new("notes_search", "search saved notes"),
    ];

    [Fact]
    public async Task Items_are_ranked_by_similarity_to_the_query()
    {
        var embedder = new KeywordEmbedder("reminder", "message", "forecast", "notes");
        var shortlist = new ToolShortlist(new ShortlistOptions(Size: 2));

        var result = await shortlist.RankAsync("move my reminder", Tools, embedder, cancellationToken: Ct);

        Assert.True(result.Ranked);
        Assert.Equal(2, result.Entries.Length);
        Assert.Equal("cron_update", result.Entries[0].Item.Name);
        Assert.True(result.Entries[0].Similarity > result.Entries[1].Similarity);
    }

    [Fact]
    public async Task Example_requests_add_vectors_and_the_best_one_counts()
    {
        // "umbrella" is in no description; an example request carries it to the weather tool (multi-vector, plan §5.4).
        var embedder = new KeywordEmbedder("reminder", "message", "forecast", "notes", "umbrella");
        var items = Tools.Select(item => item.Name == "weather" ? item with { Examples = ["do I need an umbrella today"] } : item);

        var result = await new ToolShortlist(new ShortlistOptions(Size: 1)).RankAsync("umbrella?", items, embedder, cancellationToken: Ct);

        Assert.Equal("weather", Assert.Single(result.Entries).Item.Name);
        Assert.Contains(embedder.Batches[0], text => text == "do I need an umbrella today");
    }

    [Fact]
    public async Task Hard_filters_run_before_ranking()
    {
        var embedder = new KeywordEmbedder("reminder", "message", "forecast", "notes");

        var result = await new ToolShortlist().RankAsync("move my reminder", Tools, embedder, item => item.Name != "cron_update", cancellationToken: Ct);

        Assert.DoesNotContain(result.Entries, entry => entry.Item.Name == "cron_update");
        Assert.DoesNotContain(embedder.Batches.SelectMany(batch => batch), text => text.StartsWith("cron_update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recent_use_boosts_an_item_and_the_boost_halves_with_age()
    {
        var embedder = new KeywordEmbedder("reminder", "message", "forecast", "notes");
        var shortlist = new ToolShortlist(new ShortlistOptions(RecencyWeight: 0.5, RecencyHalfLife: 1));

        var fresh = await shortlist.RankAsync("hello", Tools, embedder, usesAgo: new Dictionary<string, int> { ["weather"] = 0 }, cancellationToken: Ct);
        var older = await shortlist.RankAsync("hello", Tools, embedder, usesAgo: new Dictionary<string, int> { ["weather"] = 1 }, cancellationToken: Ct);

        var boosted = fresh.Entries.Single(entry => entry.Item.Name == "weather");
        Assert.Equal("weather", fresh.Entries[0].Item.Name);
        Assert.Equal(0.5, boosted.Score - boosted.Similarity, 6);
        var aged = older.Entries.Single(entry => entry.Item.Name == "weather");
        Assert.Equal(0.25, aged.Score - aged.Similarity, 6);
    }

    [Fact]
    public async Task Item_vectors_are_embedded_once_per_embedder()
    {
        var first = new KeywordEmbedder("reminder", "message", "forecast", "notes");
        var second = new KeywordEmbedder("reminder");
        var shortlist = new ToolShortlist();

        await shortlist.RankAsync("move my reminder", Tools, first, cancellationToken: Ct);
        await shortlist.RankAsync("send a message", Tools, first, cancellationToken: Ct);
        await shortlist.RankAsync("send a message", Tools, second, cancellationToken: Ct);

        Assert.Equal([5, 1], first.Batches.Select(batch => batch.Count));
        // A switched embedder.text starts a fresh cache: old vectors are in another space.
        Assert.Equal(5, Assert.Single(second.Batches).Count);
    }

    [Fact]
    public async Task An_embedder_failure_returns_the_filtered_items_unranked()
    {
        var embedder = new KeywordEmbedder("reminder") { Override = _ => throw new HttpRequestException("embedder down") };

        var result = await new ToolShortlist(new ShortlistOptions(Size: 3)).RankAsync("move my reminder", Tools, embedder, cancellationToken: Ct);

        Assert.False(result.Ranked);
        Assert.Equal("embedder down", result.Failure);
        Assert.Equal(["cron_update", "message_send", "notes_search"], result.Items.Select(item => item.Name));
    }
}
