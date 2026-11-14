using Corvid.Abstractions.Events;
using Corvid.Cli.Chat;
using Corvid.Host.Tests;
using Corvid.Kernel.Events;
using Corvid.Kernel.Flocks;

namespace Corvid.Cli.Tests;

/// <summary>C-042: <c>corvid flock</c>, and <c>corvid chat --flock</c> taking a flock's message at its prompt.</summary>
public sealed class FlockCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Holds the prompt open until the test opens the gate, then gives its lines; nothing is typed during a turn.</summary>
    private sealed class GatedInput(params string[] lines) : IChatInput
    {
        private readonly Queue<string> _lines = new(lines);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Echoes => false;

        public async ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
        {
            await Gate.Task.WaitAsync(cancellationToken);
            lock (_lines)
                return _lines.TryDequeue(out var line) ? line : null;
        }

        public async ValueTask<string?> ReadDuringTurnAsync(Func<string, bool> accepts, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    [Fact]
    public async Task Flocks_are_started_listed_written_to_and_shown_from_the_terminal_without_a_model()
    {
        using var run = new CliRun(new FakeModels());

        Assert.Equal(ExitCodes.Success, await run.RunAsync(["flock", "new", "auth"]));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["chat", "--flock", "auth", "--as", "api"], "/flock", "/exit"));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["flock", "say", "auth", "the", "schema", "changed"]));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["flock", "show", "auth"]));
        Assert.Equal(ExitCodes.Failure, await run.RunAsync(["flock", "say", "nowhere", "hi"]));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["flock", "close", "auth"]));
        Assert.Equal(ExitCodes.Success, await run.RunAsync(["flock", "list"]));

        var output = run.Output;
        Assert.Contains("Started the flock auth", output, StringComparison.Ordinal);
        Assert.Contains("Flock auth: this session is api; nobody else has joined yet.", output, StringComparison.Ordinal);
        Assert.Contains("Sent to everyone.", output, StringComparison.Ordinal);
        Assert.Contains("you → all: the schema changed  [waiting for 1]", output, StringComparison.Ordinal);
        Assert.Contains("not open: its messages wait", output, StringComparison.Ordinal);
        Assert.Contains("There is no flock nowhere.", output, StringComparison.Ordinal);
        Assert.Contains("No flock is open.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chat_in_a_flock_runs_a_message_that_arrives_at_its_prompt_as_its_next_turn()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Say("Updated the client."))));
        var input = new GatedInput("/exit");
        var chat = run.RunAsync(["chat", "--flock", "pair", "--as", "web"], input);
        var store = new FlockStore(new SqliteEventLog(Path.Combine(run.Root, "data", "events.db")));

        // Once the chat has joined, the user writes to the flock from elsewhere; the chat takes it at its prompt.
        var flock = await Until(() => store.Find("pair") is { Members.Length: 1 } joined ? joined : null);
        var web = flock.Members[0].SessionId;
        store.Say(flock.Id, FlockSessions.User, "move to /v2/token", [], tainted: false);
        await Until(() => store.Log.Read(web, 0, long.MaxValue, 1_000, Ct).Any(stored => stored.Payload is AssistantMessage) ? flock : null);
        input.Gate.SetResult();

        Assert.Equal(ExitCodes.Success, await chat.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Contains("✉ flock pair · you: (To everyone in the flock pair) move to /v2/token", run.Output, StringComparison.Ordinal);
        Assert.Contains("Updated the client.", run.Output, StringComparison.Ordinal);
        Assert.True(store.Get(flock.Id)!.WasDelivered(store.Get(flock.Id)!.Messages[0].Sequence, web));
    }

    private static async Task<T> Until<T>(Func<T?> probe) where T : class
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            try
            {
                if (probe() is { } value)
                    return value;
            }
            catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
            {
                // The chat may still be creating the database.
            }
            if (DateTimeOffset.UtcNow > until)
                Assert.Fail("It never happened.");
            await Task.Delay(50, Ct);
        }
    }
}
