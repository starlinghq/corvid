using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>G-5: search across sessions through the episodic index, for <c>/search</c> and the app's Ctrl+Shift+F.</summary>
public sealed class SessionSearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Titles off: the background titler would take the fake generator's scripted replies.
    private static readonly Dictionary<string, string?> NoTitles = new() { ["Corvid:Sessions:Titles"] = "false" };

    private static ValueTask<CommandOutput> Run(CorvidRuntime runtime, CorvidSession session, SlashCommandRegistry commands, string line) =>
        commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct);

    [Fact]
    public async Task A_search_finds_what_was_said_in_every_session_with_its_turn()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Noted."), Reply.Say("The zebra report is ready."), Reply.Say("Fine.")));
        await using var host = TestHost.Create(models, NoTitles);
        await using var runtime = await host.StartAsync();
        var first = runtime.OpenSession();
        await first.SendAsync("plan the release", Ct);
        await first.SendAsync("write up the zebra migration", Ct);
        var second = runtime.OpenSession();
        await second.SendAsync("notes on the zebra crossing", Ct);

        var found = runtime.SearchSessions("zebra", cancellationToken: Ct);

        Assert.Null(found.Unavailable);
        Assert.Equal(3, found.Hits.Count);
        var inFirst = found.Hits.Where(hit => hit.SessionId == first.Id).OrderBy(hit => hit.Sequence).ToList();
        Assert.Equal([("user_message", 2), ("assistant_message", 2)], inFirst.Select(hit => (hit.Kind, hit.Turn)));
        // Both sit in turn 2, whose prompt is the user's message: where a surface opens the session.
        Assert.All(inFirst, hit => Assert.Equal(inFirst[0].Sequence, hit.TurnSequence));
        var inSecond = Assert.Single(found.Hits, hit => hit.SessionId == second.Id);
        Assert.Equal((1, "you", "notes on the zebra crossing"), (inSecond.Turn, inSecond.Who, inSecond.Snippet));
        Assert.Equal("notes on the zebra crossing", inSecond.Title);
        // A word nobody said finds nothing.
        Assert.Empty(runtime.SearchSessions("giraffe", cancellationToken: Ct).Hits);
    }

    [Fact]
    public async Task Search_catches_up_with_what_another_process_wrote_to_the_log()
    {
        await using var host = TestHost.Create(null, NoTitles);
        await using var runtime = await host.StartAsync();
        // Another process (corvid chat in a terminal) wrote this session; this runtime never ran a turn of it.
        const string other = "cli-elsewhere";
        runtime.Log.Append(other, 0, [EventData.Create(new UserMessage("the quokka census is due"))], Ct);

        var hit = Assert.Single(runtime.SearchSessions("quokka", cancellationToken: Ct).Hits);

        Assert.Equal((other, 1), (hit.SessionId, hit.Turn));
    }

    // A6 review (M4): a search over an unchanged log catches up nothing and projects nothing again; a session that moved is
    // the only one read.
    [Fact]
    public async Task A_search_over_an_unchanged_log_reads_nothing_from_it()
    {
        await using var host = TestHost.Create(null, NoTitles);
        await using var runtime = await host.StartAsync();
        for (var i = 0; i < 5; i++)
            runtime.Log.Append($"cli-{i}", 0, [EventData.Create(new UserMessage($"the walrus plan {i}")), EventData.Create(new AssistantMessage("Noted."))], Ct);
        var log = new CountingLog(runtime.Log);
        var cache = new SessionSearchCache();

        var first = SessionSearch.Search(log, runtime.Memory!.Episodes, runtime.Log.ListHeads(Ct), "walrus", cache: cache, cancellationToken: Ct);
        Assert.Equal(5, first.Hits.Count);
        Assert.True(log.Reads > 0);
        log.Reset();

        var again = SessionSearch.Search(log, runtime.Memory!.Episodes, runtime.Log.ListHeads(Ct), "walrus", cache: cache, cancellationToken: Ct);

        Assert.Equal(first.Hits, again.Hits);
        Assert.Equal(0, log.Reads);

        // One session moves: only it is read (its catch-up and its projection at the new head).
        runtime.Log.Append("cli-3", 2, [EventData.Create(new UserMessage("and the walrus budget"))], Ct);
        var moved = SessionSearch.Search(log, runtime.Memory!.Episodes, runtime.Log.ListHeads(Ct), "walrus", cache: cache, cancellationToken: Ct);
        Assert.Equal(6, moved.Hits.Count);
        Assert.Equal(["cli-3"], log.Sessions);
    }

    [Fact]
    public async Task A_cancelled_search_stops()
    {
        await using var host = TestHost.Create(null, NoTitles);
        await using var runtime = await host.StartAsync();
        runtime.Log.Append("cli-x", 0, [EventData.Create(new UserMessage("the heron note"))], Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Throws<OperationCanceledException>(() => runtime.SearchSessions("heron", cancellationToken: cancelled.Token));
    }

    // A6 review (L5): one rule for /search and the palette, as for prompt history: gateway chats (other people's messages)
    // and scheduled job runs are left out, and /search says so.
    [Fact]
    public async Task Gateway_chats_and_job_runs_are_left_out_of_a_search_and_it_says_so()
    {
        await using var host = TestHost.Create(null, NoTitles);
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        runtime.Log.Append(Corvid.Gateway.GatewaySessions.Prefix + "telegram:42", 0, [EventData.Create(new UserMessage("the ibis secret from a guest"))], Ct);
        runtime.Log.Append("job:nightly:1", 0, [EventData.Create(new UserMessage("the ibis report job"))], Ct);
        runtime.Log.Append("cli-own", 0, [EventData.Create(new UserMessage("my own ibis note"))], Ct);
        var session = runtime.OpenSession();

        var hit = Assert.Single(runtime.SearchSessions("ibis", cancellationToken: Ct).Hits);
        var output = await Run(runtime, session, commands, "/search ibis");
        var none = await Run(runtime, session, commands, "/search guest");

        Assert.Equal("cli-own", hit.SessionId);
        Assert.False(CorvidRuntime.IsSearchable(Corvid.Gateway.GatewaySessions.Prefix + "slack:C1"));
        Assert.False(CorvidRuntime.IsSearchable("job:nightly:1"));
        Assert.Contains("gateway chats (other people's messages) and scheduled job runs are left out", output.Lines[0], StringComparison.Ordinal);
        Assert.Contains("gateway chats (other people's messages) and scheduled job runs are left out", none.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>An event log that counts reads, and which sessions were read, and passes everything on.</summary>
    private sealed class CountingLog(IEventLog inner) : IEventLog
    {
        private readonly List<string> _sessions = [];

        public int Reads { get; private set; }

        public IReadOnlyList<string> Sessions
        {
            get
            {
                lock (_sessions)
                    return [.. _sessions.Distinct()];
            }
        }

        public void Reset()
        {
            lock (_sessions)
            {
                Reads = 0;
                _sessions.Clear();
            }
        }

        public long GetHead(string sessionId, CancellationToken cancellationToken = default) => inner.GetHead(sessionId, cancellationToken);

        public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
            CancellationToken cancellationToken = default) => inner.Append(sessionId, expectedHead, events, cancellationToken);

        public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue, int limit = 256,
            CancellationToken cancellationToken = default)
        {
            lock (_sessions)
            {
                Reads++;
                _sessions.Add(sessionId);
            }
            return inner.Read(sessionId, afterSequence, throughSequence, limit, cancellationToken);
        }
    }

    [Fact]
    public async Task What_an_undo_took_out_of_the_conversation_is_not_a_hit()
    {
        await using var host = TestHost.Create(null, NoTitles);
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var session = runtime.OpenSession();
        await session.SendAsync("keep the pelican notes", Ct);
        await session.SendAsync("draft the pelican banner", Ct);
        Assert.Equal(2, runtime.SearchSessions("pelican", cancellationToken: Ct).Hits.Count);

        await Run(runtime, session, commands, "/undo");

        var hit = Assert.Single(runtime.SearchSessions("pelican", cancellationToken: Ct).Hits);
        Assert.Equal("keep the pelican notes", hit.Snippet);
    }

    [Fact]
    public async Task Slash_search_lists_the_hits_with_session_turn_and_snippet()
    {
        await using var host = TestHost.Create(null, NoTitles);
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var older = runtime.OpenSession();
        await older.SendAsync("the narwhal budget", Ct);
        var session = runtime.OpenSession();

        var output = await Run(runtime, session, commands, "/search narwhal");
        var empty = await Run(runtime, session, commands, "/search");
        var none = await Run(runtime, session, commands, "/search okapi");

        var row = Assert.Single(output.Table!.Rows);
        Assert.Equal(older.Id, row[0]);
        Assert.StartsWith("turn 1 · you · ", row[2], StringComparison.Ordinal);
        Assert.Equal("the narwhal budget", row[3]);
        Assert.Contains("corvid chat --session", output.Lines[0], StringComparison.Ordinal);
        Assert.Equal(OutputTone.Error, empty.Tone);
        Assert.Contains("okapi", none.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_memory_off_search_says_why()
    {
        await using var host = TestHost.Create(null, new Dictionary<string, string?>(NoTitles) { ["Corvid:Memory:Enabled"] = "false" });
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var session = runtime.OpenSession();

        var found = runtime.SearchSessions("anything", cancellationToken: Ct);
        var output = await Run(runtime, session, commands, "/search anything");

        Assert.Contains("Corvid:Memory:Enabled", found.Unavailable, StringComparison.Ordinal);
        Assert.Equal(OutputTone.Warning, output.Tone);
    }

    [Fact]
    public void A_snippet_is_one_line_around_the_match_without_hidden_characters()
    {
        var text = new string('a', 300) + " the ‮zebra​ line\nnext " + new string('b', 300);

        var snippet = SessionSearch.Snippet(text, "zebra");

        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.Contains("the zebra line next", snippet, StringComparison.Ordinal);
        Assert.DoesNotContain('‮', snippet);
        Assert.DoesNotContain('\n', snippet);
        Assert.True(snippet.Length <= SessionSearch.SnippetChars + 2);
        Assert.Equal("short and \u0001plain".Replace("\u0001", "", StringComparison.Ordinal), SessionSearch.Snippet("short and \u0001plain", "plain"));
    }

    [Theory]
    [InlineData(new long[] { }, 5, 0, 0)]
    [InlineData(new long[] { 1, 9 }, 5, 1, 1)]
    [InlineData(new long[] { 1, 9 }, 9, 2, 9)]
    [InlineData(new long[] { 1, 9 }, 40, 2, 9)]
    public void A_position_falls_in_the_latest_turn_at_or_before_it(long[] prompts, long sequence, int turn, long prompt) =>
        Assert.Equal((turn, prompt), SessionSearch.TurnAt(prompts, sequence));
}
