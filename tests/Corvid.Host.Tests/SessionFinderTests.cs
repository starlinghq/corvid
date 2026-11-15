using Corvid.Abstractions.Events;
using Corvid.Host.Sessions;
using Corvid.Kernel.Events;
using Corvid.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Tests;

/// <summary>C-044 (G-5): finding sessions by what they are about, by words and by meaning, fused with what was said.</summary>
public sealed class SessionFinderTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    // Titles off: the background titler would take the fake generator's scripted replies.
    private static readonly Dictionary<string, string?> NoTitles = new() { ["Corvid:Sessions:Titles"] = "false" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-finder-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteEventLog _log;
    private readonly MemoryDatabase _database;
    private readonly SessionIndex _index;
    private readonly EpisodicIndex _episodes;

    public SessionFinderTests()
    {
        Directory.CreateDirectory(_root);
        _log = new SqliteEventLog(Path.Combine(_root, "events.db"));
        _database = new MemoryDatabase(Path.Combine(_root, "memory.db"));
        _index = new SessionIndex(_database);
        _episodes = new EpisodicIndex(_database);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Say(string session, params SessionEvent[] events) =>
        _log.Append(session, _log.GetHead(session, Ct), [.. events.Select(EventData.Create)], Ct);

    private Dictionary<string, long> Heads() => _log.ListHeads(Ct).ToDictionary(StringComparer.Ordinal);

    private SessionFinderSources Sources(MemoryVectors? vectors = null, Func<string, string?>? branchOf = null,
        List<(string ItemId, string Text)>? embedded = null) =>
        new(_log, _index, _episodes, Heads())
        {
            Vectors = vectors,
            BranchOf = branchOf ?? (_ => null),
            Embed = cards => embedded?.AddRange(cards),
            Time = new StoppedClock(Now),
        };

    [Fact]
    public async Task By_words_a_session_is_found_by_its_title_first_message_answer_or_branch()
    {
        Say("cli-a", new UserMessage("the integration tests hang under bubblewrap"), new AssistantMessage("Retried with a longer timeout; PR #12 is up."),
            new SessionTitled("Sandbox flake in CI", "generated"));
        Say("cli-b", new UserMessage("write the release notes"), new AssistantMessage("Drafted docs/releases/v0.4.md."));
        Say("cli-c", new UserMessage("look at this"), new AssistantMessage("Done."));
        var embedded = new List<(string, string)>();

        var flake = await SessionFinder.FindAsync(Sources(embedded: embedded, branchOf: id => id == "cli-c" ? "corvid/vault-expiry" : null), "sandbox flake", cancellationToken: Ct);

        var found = Assert.Single(flake.Found);
        Assert.Null(flake.Embedder);
        Assert.Equal(("cli-a", "Sandbox flake in CI", "Retried with a longer timeout; PR #12 is up."), (found.SessionId, found.Title, found.Line));
        Assert.Equal(SessionFoundSources.Text, found.Sources);
        Assert.False(found.ByMeaning);
        // Every card was written, and each changed card was handed over to embed.
        Assert.Equal(["session:cli-a", "session:cli-b", "session:cli-c"], embedded.Select(item => item.Item1).Order());
        Assert.Contains("Sandbox flake in CI\nthe integration tests hang under bubblewrap\nRetried", embedded.Single(item => item.Item1 == "session:cli-a").Item2,
            StringComparison.Ordinal);

        Assert.Equal("cli-c", Assert.Single((await SessionFinder.FindAsync(Sources(branchOf: id => id == "cli-c" ? "corvid/vault-expiry" : null), "vault expiry",
            cancellationToken: Ct)).Found).SessionId);
        Assert.Equal("cli-b", (await SessionFinder.FindAsync(Sources(), "release notes", cancellationToken: Ct)).Found[0].SessionId);
    }

    [Fact]
    public async Task A_card_is_rewritten_only_when_its_session_moves()
    {
        Say("cli-a", new UserMessage("the walrus plan"), new AssistantMessage("Noted."));
        var embedded = new List<(string, string)>();
        await SessionFinder.FindAsync(Sources(embedded: embedded), "walrus", cancellationToken: Ct);
        Assert.Single(embedded);
        embedded.Clear();

        await SessionFinder.FindAsync(Sources(embedded: embedded), "walrus", cancellationToken: Ct);
        Assert.Empty(embedded);

        Say("cli-a", new UserMessage("now the narwhal one"), new AssistantMessage("The narwhal budget is drafted."));
        var moved = await SessionFinder.FindAsync(Sources(embedded: embedded), "narwhal budget", cancellationToken: Ct);

        Assert.Single(embedded);
        Assert.Equal(_log.GetHead("cli-a", Ct), _index.Marks()["cli-a"]);
        Assert.Equal(SessionFoundSources.Text | SessionFoundSources.Said, Assert.Single(moved.Found).Sources);
    }

    [Fact]
    public async Task By_meaning_a_session_whose_words_differ_is_found_and_fusion_ranks_both_lists_first()
    {
        // The keyword embedder puts "flaky" and "intermittent" on one axis: the second session never says "flaky".
        Say("cli-words", new UserMessage("the flaky widget"), new AssistantMessage("Looked."));
        Say("cli-meaning", new UserMessage("an intermittent failure in the sandbox suite"), new AssistantMessage("Retried."));
        Say("cli-both", new UserMessage("flaky sandbox tests"), new AssistantMessage("Fixed the flaky timeout."));
        Say("cli-none", new UserMessage("release notes"), new AssistantMessage("Drafted."));
        var cards = SessionFinder.CatchUp(Sources(), Ct);
        Assert.Equal(4, cards.Count);
        var vectors = new MemoryVectors(_database, new SqliteVectorIndex(_database),
            [new MemoryEmbedder("kw", new AxisEmbedder(("flaky", 0), ("intermittent", 0), ("sandbox", 1), ("release", 2)))]);
        vectors.BeginSwap("kw");
        await vectors.ReembedAsync(1_000, Ct);

        var result = await SessionFinder.FindAsync(Sources(vectors), "flaky", cancellationToken: Ct);

        Assert.Equal("kw", result.Embedder);
        // Found by both lists, so above the one only its meaning found; a session about nothing like it is not found at all.
        Assert.Equal(["cli-both", "cli-words"], result.Found.Take(2).Select(found => found.SessionId).Order());
        Assert.All(result.Found.Take(2), found => Assert.Equal(SessionFoundSources.Text | SessionFoundSources.Meaning, found.Sources & ~SessionFoundSources.Said));
        Assert.Equal("cli-meaning", result.Found[2].SessionId);
        Assert.Equal(SessionFoundSources.Meaning, result.Found[2].Sources);
        Assert.True(result.Found[2].ByMeaning);
        Assert.DoesNotContain(result.Found, found => found.SessionId == "cli-none");
    }

    [Fact]
    public async Task A_failed_embedder_leaves_the_search_by_words()
    {
        Say("cli-a", new UserMessage("the walrus plan"), new AssistantMessage("Noted."));
        var failing = new AxisEmbedder(("walrus", 0));
        var vectors = new MemoryVectors(_database, new SqliteVectorIndex(_database), [new MemoryEmbedder("kw", failing)]);
        vectors.BeginSwap("kw");
        SessionFinder.CatchUp(Sources(), Ct);
        await vectors.ReembedAsync(1_000, Ct);
        failing.Fail = true;

        var result = await SessionFinder.FindAsync(Sources(vectors), "walrus", cancellationToken: Ct);

        Assert.Null(result.Embedder);
        Assert.Equal("cli-a", Assert.Single(result.Found).SessionId);
    }

    [Fact]
    public async Task What_was_said_counts_for_the_session_and_a_childs_turns_for_its_parent()
    {
        Say("cli-parent", new UserMessage("tidy the docs"), new AssistantMessage("Delegated."));
        Say("child-1", new ChildOf("cli-parent", 1, "check links", [], new ChildBudget(4, 8_000, 120)),
            new UserMessage("check links"), new AssistantMessage("The okapi page has a broken link."));

        var result = await SessionFinder.FindAsync(Sources(), "okapi", cancellationToken: Ct);

        var found = Assert.Single(result.Found);
        Assert.Equal(("cli-parent", SessionFoundSources.Said), (found.SessionId, found.Sources));
        Assert.Equal("child-1", found.Turn!.SessionId);
        Assert.Equal("The okapi page has a broken link.", found.Line);
        // A child never gets a card of its own.
        Assert.DoesNotContain("child-1", _index.Marks().Keys);
    }

    [Fact]
    public async Task Recent_activity_lifts_a_session()
    {
        _log.Append("cli-a", 0, [EventData.Create(new UserMessage("the zebra report"))], Ct);

        var stale = await SessionFinder.FindAsync(Sources() with { Time = new StoppedClock(DateTimeOffset.UtcNow.AddYears(1)) }, "zebra report", cancellationToken: Ct);
        var fresh = await SessionFinder.FindAsync(Sources() with { Time = new StoppedClock(DateTimeOffset.UtcNow) }, "zebra report", cancellationToken: Ct);

        // A year idle leaves almost none of the boost; activity just now gets all of it (half again the fused score).
        Assert.Equal(Assert.Single(stale.Found).Score * 1.5, Assert.Single(fresh.Found).Score, 3);
    }

    [Fact]
    public async Task The_runtime_finds_sessions_by_words_without_an_embedder_and_says_why_with_memory_off()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("The sandbox flake is fixed."), Reply.Say("Drafted.")));
        await using (var host = TestHost.Create(models, NoTitles))
        await using (var runtime = await host.StartAsync())
        {
            var first = runtime.OpenSession();
            await first.SendAsync("look into the CI hang", Ct);
            var second = runtime.OpenSession();
            await second.SendAsync("release notes", Ct);

            var found = await runtime.FindSessionsAsync("sandbox flake", cancellationToken: Ct);

            Assert.Null(found.Unavailable);
            Assert.Equal(first.Id, Assert.Single(found.Found).SessionId);
            Assert.Equal(2, runtime.Memory!.Sessions!.Count());
        }

        await using var off = TestHost.Create(null, new Dictionary<string, string?>(NoTitles) { ["Corvid:Memory:Enabled"] = "false" });
        await using var bare = await off.StartAsync();
        var none = await bare.FindSessionsAsync("anything", cancellationToken: Ct);
        Assert.Contains("Corvid:Memory:Enabled", none.Unavailable, StringComparison.Ordinal);
        Assert.Empty(none.Found);
    }

    /// <summary>Embeds by keyword onto chosen axes, so two words can share a meaning; can be made to fail.</summary>
    private sealed class AxisEmbedder(params (string Word, int Axis)[] words) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public bool Fail { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new HttpRequestException("embedder down");
            var axes = words.Max(word => word.Axis) + 2;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text =>
            {
                var vector = new float[axes];
                foreach (var (word, axis) in words)
                    if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
                        vector[axis] = 1;
                vector[^1] = 0.1f;
                return new Embedding<float>(vector);
            })));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
