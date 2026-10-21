using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Sessions;

namespace Corvid.Kernel.Tests;

public sealed class SessionForkTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonElement Args = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone();
    private readonly TempLog _db = new();

    public void Dispose() => _db.Dispose();

    // 1 user, 2 call, 3 result, 4 assistant, 5 user, 6 call (no result yet).
    private void Source() => _db.Log.Append("src", 0, Samples.Data(
        new UserMessage("read a.txt"),
        new ToolCallIssued("c1", "read_file", Args),
        new ToolResult("c1", JsonDocument.Parse("\"hello\"").RootElement.Clone()),
        new AssistantMessage("It says hello."),
        new UserMessage("again"),
        new ToolCallIssued("c2", "read_file", Args)), cancellationToken: Ct);

    [Fact]
    public void A_fork_copies_the_events_through_the_position_at_the_same_sequences()
    {
        Source();

        Assert.Equal(4, SessionFork.Fork(_db.Log, "src", "fork", 4, Ct));

        var copied = _db.Log.Read("fork", cancellationToken: Ct);
        var original = _db.Log.Read("src", 0, 4, cancellationToken: Ct);
        Assert.Equal(original.Select(item => item.Sequence), copied.Select(item => item.Sequence));
        Assert.Equal(original.Select(item => item.Payload.GetType()), copied.Select(item => item.Payload.GetType()));
        Assert.Equal("It says hello.", Assert.IsType<AssistantMessage>(copied[^1].Payload).Text);
        Assert.DoesNotContain(copied, item => original.Any(source => source.EventId == item.EventId));
        // The source is untouched.
        Assert.Equal(6, _db.Log.GetHead("src", cancellationToken: Ct));
    }

    [Theory]
    [InlineData(2L)]
    [InlineData(6L)]
    public void A_position_that_leaves_a_call_without_its_result_is_refused(long position)
    {
        Source();

        var refused = Assert.Throws<SessionForkException>(() => SessionFork.Fork(_db.Log, "src", "fork", position, Ct));
        Assert.Contains("without its result", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, _db.Log.GetHead("fork", cancellationToken: Ct));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(7L)]
    public void A_position_outside_the_log_is_refused(long position)
    {
        Source();

        Assert.Throws<SessionForkException>(() => SessionFork.Fork(_db.Log, "src", "fork", position, Ct));
    }

    [Fact]
    public void Missing_sources_existing_targets_and_self_forks_are_refused()
    {
        Assert.Throws<SessionForkException>(() => SessionFork.Fork(_db.Log, "nobody", "fork", null, Ct));
        Source();
        _db.Log.Append("taken", 0, Samples.Data(new UserMessage("mine")), cancellationToken: Ct);
        Assert.Throws<SessionForkException>(() => SessionFork.Fork(_db.Log, "src", "taken", 4, Ct));
        Assert.Throws<SessionForkException>(() => SessionFork.Fork(_db.Log, "src", "src", 4, Ct));
        Assert.Equal(1, _db.Log.GetHead("taken", cancellationToken: Ct));
    }
}
