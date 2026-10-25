using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory.Tests;

public sealed class EpisodicIndexTests
{
    private const string Session = "s-1";

    private static CancellationToken Ct => Fixtures.Ct;

    private static void Append(TempStore store, params SessionEvent[] events) =>
        store.Log.Append(Session, store.Log.GetHead(Session, Ct), [.. events.Select(EventData.Create)], Ct);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Catch_up_indexes_text_events_once_and_skips_fabric_bookkeeping()
    {
        using var store = new TempStore();
        Append(store,
            new UserMessage("move the dentist reminder to friday"),
            new Decision("d-1", "intent", ImmutableDictionary<string, double>.Empty, 0, 0.5, "route"),
            new ToolCallIssued("c-1", "cron.update", Json("""{"id":14,"day":"friday"}""")),
            new ToolResult("c-1", Json("""{"ok":true,"note":"moved"}""")),
            new AssistantMessage("Done, the dentist is on Friday."));

        var added = store.Episodes.CatchUp(store.Log, Session, Ct);
        Assert.Equal(["user_message", "tool_call_issued", "tool_result", "assistant_message"], added.Select(e => e.Kind));
        Assert.Equal(5, store.Episodes.IndexedThrough(Session));
        Assert.Equal("cron.update id: 14\nday: friday", added[1].Text);
        Assert.Empty(store.Episodes.CatchUp(store.Log, Session, Ct));

        Append(store, new UserMessage("and the vet on monday"));
        Assert.Equal(6, Assert.Single(store.Episodes.CatchUp(store.Log, Session, Ct)).Sequence);
    }

    [Fact]
    public void A_local_only_shell_line_is_never_indexed()
    {
        // A4: the composer's `!!` line keeps its output from the model; recall would hand it back later.
        using var store = new TempStore();
        Append(store,
            new UserMessage("!!cat diary.txt", LocalOnly: true),
            new ToolCallIssued("user-shell", "shell", Json("""{"command":"cat diary.txt"}"""), LocalOnly: true),
            new ToolResult("user-shell", Json("""{"stdout":"dear diary"}"""), LocalOnly: true),
            new UserMessage("what time is it"));

        var added = store.Episodes.CatchUp(store.Log, Session, Ct);

        Assert.Equal("what time is it", Assert.Single(added).Text);
        Assert.Empty(store.Episodes.Search("diary", 5));
    }

    [Fact]
    public void Search_ranks_by_bm25_and_can_be_limited_to_a_session()
    {
        using var store = new TempStore();
        Append(store, new UserMessage("the dentist appointment"), new UserMessage("dentist moved, dentist confirmed for friday"),
            new UserMessage("groceries"));
        store.Log.Append("s-2", 0, [EventData.Create(new UserMessage("another dentist elsewhere"))], Ct);
        store.Episodes.CatchUp(store.Log, Session, Ct);
        store.Episodes.CatchUp(store.Log, "s-2", Ct);

        var hits = store.Episodes.Search("dentist friday", sessionId: Session);
        Assert.Equal([2, 1], hits.Select(hit => hit.Episode.Sequence));
        Assert.True(hits[0].Rank < hits[1].Rank);
        Assert.Equal(3, store.Episodes.Search("dentist").Count);
        Assert.Empty(store.Episodes.Search("  ,. "));
    }

    [Fact]
    public void Text_without_word_breaks_is_searchable_including_short_terms()
    {
        using var store = new TempStore();
        Append(store,
            new UserMessage("我住在东京塔附近的公寓"),
            new UserMessage("明日は大阪城に行きます"),
            new ToolResult("c-1", Json("""{"city":"東京","note":"晴れ"}""")));
        store.Episodes.CatchUp(store.Log, Session, Ct);

        Assert.Equal(1, Assert.Single(store.Episodes.Search("东京塔")).Episode.Sequence);
        Assert.Equal(2, Assert.Single(store.Episodes.Search("大阪城に")).Episode.Sequence);
        // Two characters cannot use the trigram index; they fall back to LIKE, newest first.
        Assert.Equal([3], store.Episodes.Search("東京").Select(hit => hit.Episode.Sequence));
        Assert.Equal(0, store.Episodes.Search("東京").Single().Rank);
    }

    [Fact]
    public void Compacted_ranges_stay_searchable_and_summaries_are_indexed()
    {
        using var store = new TempStore();
        Append(store, new UserMessage("the build uses bubblewrap"), new AssistantMessage("noted"));
        Append(store, new Compacted(1, 2, new CompactionSummary("sandbox the shell", ["no network"], ["use landlock"], [], [], null)));
        store.Episodes.CatchUp(store.Log, Session, Ct);

        Assert.Equal(1, Assert.Single(store.Episodes.Search("bubblewrap")).Episode.Sequence);
        Assert.Equal("compacted", Assert.Single(store.Episodes.Search("landlock")).Episode.Kind);
    }

    [Fact]
    public void Search_stays_in_step_with_its_rows_after_vacuum()
    {
        using var store = new TempStore();
        Append(store, new UserMessage("first dentist"), new UserMessage("second groceries"), new UserMessage("third dentist"));
        store.Episodes.CatchUp(store.Log, Session, Ct);
        store.Facts.Add(new FactInput("Sam likes tea"));
        using (var connection = store.Database.Open())
        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }
        Assert.Equal([3, 1], store.Episodes.Search("dentist").Select(hit => hit.Episode.Sequence).Order().Reverse());
        Assert.Equal("second groceries", Assert.Single(store.Episodes.Search("groceries")).Episode.Text);
        Assert.Equal("Sam likes tea", Assert.Single(store.Facts.Search("tea")).Fact.Text);
    }

    [Fact]
    public void Episodes_cannot_be_changed_or_deleted()
    {
        using var store = new TempStore();
        Append(store, new UserMessage("keep this"));
        store.Episodes.CatchUp(store.Log, Session, Ct);
        using var connection = store.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE episodes SET text='changed';";
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).Message);
        command.CommandText = "DELETE FROM episodes;";
        Assert.Contains("append-only", Assert.Throws<SqliteException>(() => command.ExecuteNonQuery()).Message);
    }

    [Theory]
    [InlineData("dentist on friday", "\"dentist\" OR \"friday\"", new string[] { "on" })]
    [InlineData("say \"hi\" to O'Brien", "\"say\" OR \"Brien\"", new[] { "hi", "to", "O" })]
    [InlineData("Dentist dentist", "\"Dentist\"", new string[0])]
    public void Fts_queries_quote_terms_and_set_short_ones_aside(string query, string match, string[] shortTerms)
    {
        var (built, rest) = FtsQuery.Build(query);
        Assert.Equal(match, built);
        Assert.Equal(shortTerms, rest);
    }
}
