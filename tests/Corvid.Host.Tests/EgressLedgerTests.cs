using System.Collections.Immutable;
using System.Text;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Host.Audit;
using Corvid.Kernel.Events;
using Corvid.Models;
using Corvid.Models.Privacy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Tests;

/// <summary>The egress ledger (G-9): each gated request recorded in the session's log, its text as sent kept beside it.</summary>
public sealed class EgressLedgerTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A GitHub token's shape: the credential detector finds it whatever the policy, and the gate never sends it.
    private const string Token40 = "ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789";

    private static readonly ModelProfile Cloud = new("claude@anthropic", 200_000, 200_000, ToolCallSupport.Native,
        StructuredOutputSupport.None, PromptCachingStyle.Breakpoints, ModelCost.Free, PrivacyClass.Cloud, "api.anthropic.com");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-egress-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteEventLog _log;
    private readonly EgressLedger _ledger;
    private readonly List<string> _failures = [];
    // Stands at the real time (some tests count from now) until a retention test moves it.
    private readonly StoppedClock _clock = new(DateTimeOffset.UtcNow);

    public EgressLedgerTests()
    {
        Directory.CreateDirectory(_root);
        _log = new SqliteEventLog(Path.Combine(_root, "events.db"));
        _ledger = new EgressLedger(Path.Combine(_root, "egress.db"), _clock) { Log = _log, Failed = _failures.Add };
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

    private sealed class Pii(params (string Label, string Value)[] known) : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var spans = ImmutableArray.CreateBuilder<ExtractedSpan>();
            foreach (var (label, value) in known.Where(item => request.Labels.Contains(item.Label)))
                for (var at = request.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = request.Text.IndexOf(value, at + 1, StringComparison.Ordinal))
                    spans.Add(new ExtractedSpan(label, value, at, at + value.Length, 0.9f));
            return Task.FromResult(new ExtractionResult(spans.ToImmutable()));
        }
    }

    private PrivacyEgressChatClient Client(EgressPolicy? policy = null, FakeGenerator? provider = null) =>
        new(provider ?? new FakeGenerator(Reply.Say("ok"), Reply.Say("ok"), Reply.Say("ok")), Cloud,
            new PrivacyEgress(new Pii(("person", "Alice Smith"), ("email", "alice@example.com")),
                new PrivacyEgressOptions { PolicyFor = _ => policy ?? EgressPolicy.Default }),
            _ledger.For("generator", Cloud.Placement, "anthropic.key"));

    private static ChatOptions Session(string session) => new ModelRequestContext(session, Tainted: false).Apply(new ChatOptions());

    private static List<ChatMessage> First() =>
    [
        new(ChatRole.System, "You are Corvid."),
        new(ChatRole.User, $"Email Alice Smith at alice@example.com; the token is {Token40}."),
    ];

    private static List<ChatMessage> Second() =>
    [
        .. First(),
        new(ChatRole.Assistant, "I will write to Alice Smith."),
        new(ChatRole.User, "Go ahead."),
    ];

    private List<EgressRecorded> Recorded(string session) =>
        [.. _log.Read(session, cancellationToken: Token).Select(item => item.Payload).OfType<EgressRecorded>()];

    [Fact]
    public async Task A_redacted_request_is_recorded_in_its_session_and_kept_as_sent_with_no_value_and_no_credential()
    {
        var client = Client();
        await client.GetResponseAsync(First(), Session("s1"), Token);
        await client.GetResponseAsync(Second(), Session("s1"), Token);

        var events = Recorded("s1");
        Assert.Equal(2, events.Count);
        var second = events[1];
        Assert.Equal((EgressOutcomes.Redacted, "claude@anthropic", "generator", "api.anthropic.com", "anthropic.key", null),
            (second.Outcome, second.Model, second.Slot, second.Host, second.Secret, second.Unkept));
        // Alice Smith, her address and the token: three values, each one placeholder however often it appears.
        Assert.Equal(3, second.Placeholders);
        Assert.Equal(1, second.Labels["person"]);

        var entries = _ledger.List("s1", cancellationToken: Token);
        Assert.Equal([2L, 1L], entries.Select(entry => entry.Number));
        Assert.Equal(second.RequestId, entries[0].RequestId);
        Assert.True(entries[0].Kept);

        var full = _ledger.Render(second.RequestId)!;
        Assert.Contains("[system] You are Corvid.", full, StringComparison.Ordinal);
        Assert.Contains("[user] Email <PERSON_1> at <EMAIL_1>; the token is <CREDENTIAL_1>.", full, StringComparison.Ordinal);
        Assert.Contains("[assistant] I will write to <PERSON_1>.", full, StringComparison.Ordinal);
        Assert.Contains("[user] Go ahead.", full, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", full, StringComparison.Ordinal);
        Assert.Equal(second.Bytes, Encoding.UTF8.GetByteCount(full));
        Assert.Equal(second.Placeholders, EgressRendering.Placeholders(full));

        // The shared prefix is kept once: system and first message, then the answer and the second message.
        Assert.Equal(4, Scalar("SELECT COUNT(*) FROM messages"));
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_root, "egress.db*"))
        {
            var bytes = await File.ReadAllBytesAsync(file, Token);
            Assert.False(Contains(bytes, "Alice Smith"), file);
            Assert.False(Contains(bytes, "alice@example.com"), file);
            Assert.False(Contains(bytes, Token40), file);
        }
        Assert.Empty(_failures);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_ledger.Path));
    }

    [Fact]
    public async Task A_refused_request_counts_but_nothing_of_it_is_kept()
    {
        var client = Client(EgressPolicy.Default with { Action = EgressAction.Refuse });
        await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() => client.GetResponseAsync(First(), Session("s2"), Token));

        var refused = Assert.Single(Recorded("s2"));
        Assert.Equal((EgressOutcomes.Refused, 0L, 0), (refused.Outcome, refused.Bytes, refused.Placeholders));
        var entry = Assert.Single(_ledger.List("s2", cancellationToken: Token));
        Assert.False(entry.Kept);
        Assert.False(entry.Left);
        Assert.Null(_ledger.Render(entry.RequestId));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM messages"));

        await Client().GetResponseAsync(First(), Session("s3"), Token);
        // Two requests, the three values of the one that left, the one refused.
        Assert.Equal(new EgressCounts(2, 3, 1), _ledger.Count(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Equal(new EgressCounts(0, 0, 0), _ledger.Count(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(2, _ledger.List(cancellationToken: Token).Count);
    }

    [Fact]
    public async Task A_request_outside_any_session_is_in_the_ledger_but_in_no_log()
    {
        await Client().GetResponseAsync(First(), new ChatOptions(), Token);

        var entry = Assert.Single(_ledger.List(cancellationToken: Token));
        Assert.Null(entry.SessionId);
        Assert.Empty(_log.ListSessions(Token));
    }

    [Fact]
    public async Task A_store_that_cannot_be_written_loses_the_text_but_the_log_still_says_the_request_left()
    {
        // A directory where the store's file should be: SQLite cannot open it.
        var blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "egress.db"));
        var failures = new List<string>();
        var ledger = new EgressLedger(Path.Combine(blocked, "egress.db")) { Log = _log, Failed = failures.Add };
        var client = new PrivacyEgressChatClient(new FakeGenerator(Reply.Say("ok")), Cloud,
            new PrivacyEgress(new Pii(), new PrivacyEgressOptions { PolicyFor = _ => EgressPolicy.Default }), ledger.For("generator"));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], Session("s4"), Token);

        Assert.Equal("ok", response.Text);
        var recorded = Assert.Single(Recorded("s4"));
        Assert.Equal(EgressOutcomes.Sent, recorded.Outcome);
        Assert.StartsWith("the egress store could not be written", recorded.Unkept, StringComparison.Ordinal);
        Assert.Single(failures);
    }

    [Fact]
    public void Readers_never_create_the_store()
    {
        var ledger = new EgressLedger(Path.Combine(_root, "none", "egress.db"));

        Assert.Empty(ledger.List(cancellationToken: Token));
        Assert.Null(ledger.Find("1"));
        Assert.Null(ledger.Render("x"));
        Assert.Equal(new EgressCounts(0, 0, 0), ledger.Count(DateTimeOffset.MinValue));
        Assert.False(File.Exists(ledger.Path));
    }

    [Fact]
    public void The_rendering_names_each_part_and_keeps_tool_calls_and_results()
    {
        var blocks = EgressRendering.Blocks(
        [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "shell", new Dictionary<string, object?> { ["command"] = "ls -la" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "a.txt\nb.txt")]),
        ]);
        var full = EgressRendering.Full(blocks, ["shell", "read_file"]);

        Assert.Equal("[tools offered] shell, read_file\n[tool_call c1 shell] {\"command\":\"ls -la\"}\n[tool_result c1]\na.txt\nb.txt", full);
        Assert.Equal(full, EgressRendering.Excerpt(full));
        // A JSON result keeps a placeholder readable (and counted), however its text escaped it.
        using var escaped = System.Text.Json.JsonDocument.Parse("""{"path":"<EMAIL_2>","args":{"to":"<PERSON_1>"}}""");
        var result = EgressRendering.Blocks([new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c2", escaped.RootElement.Clone()),
            new FunctionCallContent("c3", "send", new Dictionary<string, object?> { ["to"] = escaped.RootElement.GetProperty("args").Clone() })])]);
        Assert.Equal("[tool_result c2]\n{\"path\":\"<EMAIL_2>\",\"args\":{\"to\":\"<PERSON_1>\"}}\n[tool_call c3 send] {\"to\":{\"to\":\"<PERSON_1>\"}}", result[0]);
        Assert.Equal(2, EgressRendering.Placeholders(result[0]));
        var many = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}"));
        var excerpt = EgressRendering.Excerpt(many);
        Assert.Equal(17, excerpt.Split('\n').Length);
        Assert.StartsWith("line 1\n", excerpt, StringComparison.Ordinal);
        Assert.EndsWith("line 30", excerpt, StringComparison.Ordinal);
        Assert.Contains("\n…\n", excerpt, StringComparison.Ordinal);
    }

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_ledger.Path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static bool Contains(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private static List<ChatMessage> Other() => [new(ChatRole.System, "You are Corvid."), new(ChatRole.User, "Summarise the changelog.")];

    /// <summary>A4 review M2: requests past the retention go, then the bodies no request uses; at most daily while recording.</summary>
    [Fact]
    public async Task Requests_older_than_the_retention_are_pruned_with_the_messages_only_they_used()
    {
        _ledger.Retention = new EgressRetention(TimeSpan.FromDays(90), null);
        var client = Client();
        await client.GetResponseAsync(First(), Session("old"), Token);
        await client.GetResponseAsync(Second(), Session("old"), Token);
        await _ledger.Pruning;
        Assert.Equal(4, Scalar("SELECT COUNT(*) FROM messages"));

        _clock.Now = _clock.Now.AddDays(91);
        await client.GetResponseAsync(Other(), Session("new"), Token);
        // The day since the store's last prune has passed: recording pruned it, in the background.
        await _ledger.Pruning;

        Assert.Equal(["new"], _ledger.List(cancellationToken: Token).Select(entry => entry.SessionId));
        // The system prompt the new request shares stays; the old conversation's three other messages went.
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM messages"));
        Assert.Contains("Summarise the changelog.", _ledger.Render(_ledger.List(cancellationToken: Token)[0].RequestId), StringComparison.Ordinal);
        // Not again the same day.
        Assert.False(_ledger.PruneIfDue());
        _clock.Now = _clock.Now.AddDays(1);
        Assert.True(_ledger.PruneIfDue());
        Assert.Empty(_failures);
    }

    [Fact]
    public async Task Over_the_size_cap_the_oldest_requests_go_first_and_prune_compacts_on_request()
    {
        _ledger.Retention = new EgressRetention(null, null);
        var client = Client();
        await client.GetResponseAsync(First(), Session("s1"), Token);
        await client.GetResponseAsync(Other(), Session("s2"), Token);
        await _ledger.Pruning;
        var newest = Scalar("SELECT LENGTH(CAST(body AS BLOB)) FROM messages WHERE body LIKE '%changelog%'")
            + Scalar("SELECT LENGTH(CAST(body AS BLOB)) FROM messages WHERE body LIKE '%You are Corvid.%'");

        _ledger.Retention = new EgressRetention(null, newest);
        var pruned = _ledger.Prune(vacuum: true);

        Assert.Equal((1, 1), (pruned.Requests, pruned.Messages));
        Assert.Equal(["s2"], _ledger.List(cancellationToken: Token).Select(entry => entry.SessionId));
        Assert.Empty(_failures);
    }

    [Fact]
    public void Prune_on_no_store_creates_none()
    {
        Assert.Equal(new EgressPruned(0, 0), _ledger.Prune());
        Assert.False(File.Exists(_ledger.Path));
    }
}
