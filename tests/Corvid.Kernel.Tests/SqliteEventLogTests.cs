using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Microsoft.Data.Sqlite;

namespace Corvid.Kernel.Tests;

public sealed class SqliteEventLogTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempLog _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Sequences_are_one_based_and_increase_across_batches()
    {
        var log = _db.Log;
        Assert.Equal(0, log.GetHead("s", cancellationToken: Ct));

        var first = log.Append("s", 0, Samples.Data(new UserMessage("a"), new AssistantMessage("b")), cancellationToken: Ct);
        var second = log.Append("s", 2, Samples.Data(new UserMessage("c")), cancellationToken: Ct);

        Assert.Equal([1L, 2L], first.Select(item => item.Sequence));
        Assert.Equal(3, Assert.Single(second).Sequence);
        Assert.Equal(3, log.GetHead("s", cancellationToken: Ct));
        Assert.Equal([1L, 2L, 3L], log.Read("s", cancellationToken: Ct).Select(item => item.Sequence));
    }

    [Fact]
    public void Sessions_have_independent_sequences()
    {
        _db.Log.Append("a", 0, Samples.Data(new UserMessage("a1"), new UserMessage("a2")), cancellationToken: Ct);
        _db.Log.Append("b", 0, Samples.Data(new UserMessage("b1")), cancellationToken: Ct);

        Assert.Equal(2, _db.Log.GetHead("a", cancellationToken: Ct));
        Assert.Equal(1, _db.Log.GetHead("b", cancellationToken: Ct));
        Assert.Equal("b1", Assert.IsType<UserMessage>(Assert.Single(_db.Log.Read("b", cancellationToken: Ct)).Payload).Text);
    }

    [Fact]
    public void Stale_expected_head_is_a_conflict_and_appends_nothing()
    {
        _db.Log.Append("s", 0, Samples.Data(new UserMessage("a")), cancellationToken: Ct);

        var conflict = Assert.Throws<EventLogConflictException>(() =>
            _db.Log.Append("s", 0, Samples.Data(new UserMessage("lost")), cancellationToken: Ct));

        Assert.Equal((0L, 1L), (conflict.ExpectedHead, conflict.ActualHead));
        Assert.Equal(1, _db.Log.GetHead("s", cancellationToken: Ct));
    }

    [Fact]
    public void A_failing_batch_appends_nothing()
    {
        var existing = EventData.Create(new UserMessage("a"));
        _db.Log.Append("s", 0, [existing], cancellationToken: Ct);

        // The second event reuses an event ID, so the first must roll back with it.
        Assert.Throws<SqliteException>(() =>
            _db.Log.Append("s", 1, [EventData.Create(new UserMessage("b")), existing with { Payload = new UserMessage("c") }], cancellationToken: Ct));

        Assert.Equal(1, _db.Log.GetHead("s", cancellationToken: Ct));
    }

    [Fact]
    public void Compaction_must_reference_prior_events_in_the_same_batch_order()
    {
        _db.Log.Append("s", 0, Samples.Data(new UserMessage("a")), cancellationToken: Ct);
        var summary = new CompactionSummary(null, [], [], [], [], null);

        Assert.Throws<ArgumentException>(() => _db.Log.Append("s", 1, Samples.Data(new Compacted(1, 2, summary)), cancellationToken: Ct));
        Assert.Throws<ArgumentException>(() => _db.Log.Append("s", 1, Samples.Data(new Compacted(0, 1, summary)), cancellationToken: Ct));
        // Sequence 2 exists by the time the summary at 3 is written.
        _db.Log.Append("s", 1, Samples.Data(new AssistantMessage("b"), new Compacted(1, 2, summary)), cancellationToken: Ct);

        Assert.Equal(3, _db.Log.GetHead("s", cancellationToken: Ct));
    }

    [Theory]
    [InlineData("UPDATE events SET payload='{}'")]
    [InlineData("DELETE FROM events")]
    public void The_database_refuses_updates_and_deletes(string sql)
    {
        _db.Log.Append("s", 0, Samples.Data(new UserMessage("a")), cancellationToken: Ct);
        using var connection = _db.OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, _db.Log.GetHead("s", cancellationToken: Ct));
    }

    [Fact]
    public void Every_event_type_survives_the_database_and_a_reopen()
    {
        var events = Samples.All();
        var appended = _db.Log.Append("s", 0, Samples.Data([.. events]), cancellationToken: Ct);

        var read = _db.Reopen().Read("s", cancellationToken: Ct);

        Assert.Equal(events.Count, read.Count);
        for (var i = 0; i < read.Count; i++)
        {
            Assert.Equal(appended[i].EventId, read[i].EventId);
            Assert.Equal(appended[i].RecordedAt, read[i].RecordedAt);
            Assert.Equal(EventJson.Serialize(events[i]), EventJson.Serialize(read[i].Payload));
        }
    }

    [Fact]
    public void Reads_are_a_window_after_a_position()
    {
        _db.Log.Append("s", 0, Samples.Data([.. Enumerable.Range(1, 10).Select(i => new UserMessage($"m{i}"))]), cancellationToken: Ct);

        Assert.Equal([4L, 5L, 6L], _db.Log.Read("s", afterSequence: 3, throughSequence: 6, cancellationToken: Ct).Select(item => item.Sequence));
        Assert.Equal([8L, 9L], _db.Log.Read("s", afterSequence: 7, limit: 2, cancellationToken: Ct).Select(item => item.Sequence));
        Assert.Empty(_db.Log.Read("s", afterSequence: 10, cancellationToken: Ct));
    }

    [Fact]
    public void Appended_payloads_do_not_alias_caller_json()
    {
        var document = System.Text.Json.JsonDocument.Parse("""{"path":"a.txt"}""");
        var stored = _db.Log.Append("s", 0, Samples.Data(new ToolCallIssued("c", "read", document.RootElement)), cancellationToken: Ct);
        document.Dispose();

        var call = Assert.IsType<ToolCallIssued>(Assert.Single(stored).Payload);
        Assert.Equal("a.txt", call.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void A_newer_schema_version_is_refused()
    {
        using (var connection = _db.OpenRaw())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(_db.Reopen);
    }

    [Fact]
    public void Concurrent_writers_never_lose_or_duplicate_a_sequence()
    {
        // Two handles on one file, as a session actor and a recovering process would have.
        const int writers = 4, perWriter = 15;
        var logs = new[] { _db.Log, _db.Reopen() };
        var conflicts = 0;

        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = writers }, writer =>
        {
            var log = logs[writer % logs.Length];
            for (var i = 0; i < perWriter; i++)
            {
                while (true)
                {
                    var head = log.GetHead("s", cancellationToken: Ct);
                    try
                    {
                        log.Append("s", head, Samples.Data(new UserMessage($"{writer}:{i}")), cancellationToken: Ct);
                        break;
                    }
                    catch (EventLogConflictException)
                    {
                        Interlocked.Increment(ref conflicts);
                    }
                }
            }
        });

        var all = _db.Log.Read("s", limit: 4096, cancellationToken: Ct);
        Assert.Equal(Enumerable.Range(1, writers * perWriter).Select(i => (long)i), all.Select(item => item.Sequence));
        Assert.Equal(writers * perWriter, all.Select(item => ((UserMessage)item.Payload).Text).Distinct().Count());
        // Each writer's own events keep their order.
        for (var writer = 0; writer < writers; writer++)
        {
            var mine = all.Select(item => ((UserMessage)item.Payload).Text).Where(text => text.StartsWith($"{writer}:", StringComparison.Ordinal));
            Assert.Equal(Enumerable.Range(0, perWriter).Select(i => $"{writer}:{i}"), mine);
        }
    }

    [Fact]
    public void Arguments_are_validated()
    {
        Assert.Throws<ArgumentException>(() => new SqliteEventLog(":memory:"));
        Assert.Throws<ArgumentException>(() => _db.Log.Append("s", 0, [], cancellationToken: Ct));
        Assert.Throws<ArgumentException>(() => _db.Log.Append("s", 0, [new EventData(Guid.Empty, new UserMessage("a"))], cancellationToken: Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.Log.Append("s", -1, Samples.Data(new UserMessage("a")), cancellationToken: Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.Log.Read("s", limit: 0, cancellationToken: Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => _db.Log.Read("s", afterSequence: 5, throughSequence: 4, cancellationToken: Ct));
        Assert.Equal(0, _db.Log.GetHead("s", cancellationToken: Ct));
    }
}
