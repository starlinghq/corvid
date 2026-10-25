using Corvid.Cli.Chat;
using Corvid.Host.Tests;

namespace Corvid.Cli.Tests;

/// <summary>G-15 in <c>corvid chat</c>: <c>/fork</c> typed while a turn runs forks at its last step, and the turn goes on.</summary>
public sealed class ChatForkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Ordinary reads from a script; reads during a turn hand out the lines a test gives them, each once the turn has reached
    /// the point the test waits for, then wait until the turn ends (the chat cancels them).
    /// </summary>
    private sealed class TurnInput(Task reached, IEnumerable<string?> lines, params string[] duringTurn) : IChatInput
    {
        private readonly Queue<string?> _lines = new(lines);
        private readonly Queue<string> _duringTurn = new(duringTurn);
        private readonly Queue<string> _putBack = new();

        public TaskCompletionSource DuringTurnDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Echoes => false;

        public bool ReadsDuringTurn => true;

        public ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
        {
            if (!fresh && _putBack.TryDequeue(out var back))
                return ValueTask.FromResult<string?>(back);
            return ValueTask.FromResult(_lines.TryDequeue(out var next) ? next : null);
        }

        public async ValueTask<string?> ReadDuringTurnAsync(CancellationToken cancellationToken)
        {
            await reached.WaitAsync(cancellationToken);
            if (_duringTurn.TryDequeue(out var line))
                return line;
            DuringTurnDone.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }

        public void PutBack(string line) => _putBack.Enqueue(line);
    }

    [Fact]
    public async Task Fork_typed_during_a_turn_forks_at_its_last_step_and_a_message_typed_meanwhile_waits_for_the_turn()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TurnInput? input = null;
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes."), Reply.Say("Water the plants next."))
        {
            // The turn's second generator call waits until the lines typed during it have been handled. (The bound only
            // turns a chat that never reads during a turn into a failed assertion rather than a hang.)
            Hold = (step, _) =>
            {
                if (step != 1)
                    return null;
                reached.TrySetResult();
                return input!.DuringTurnDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
            },
        };
        using var run = new CliRun(new FakeModels(generator));
        input = new TurnInput(reached.Task, ["list my notes", "/exit"], "what next?", "/fork");
        var context = new CliContext(run.Console, input) { ConfigureServices = services => run.Models!.Register(services) };

        var exit = await CorvidCli.RunAsync([.. run.Settings(), "chat"], context, Ct);

        Assert.Equal(ExitCodes.Success, exit);
        var output = run.Output;
        var forked = output.IndexOf("Forked into cli-", StringComparison.Ordinal);
        Assert.True(forked >= 0, output);
        Assert.Contains("the running turn's last completed step; the turn goes on here.", output, StringComparison.Ordinal);
        Assert.Contains("Continue it with corvid chat --session cli-", output, StringComparison.Ordinal);
        // The fork came while the turn ran: before its answer. The message typed meanwhile ran after it, as its own turn.
        var answer = output.IndexOf("You have two notes.", StringComparison.Ordinal);
        var next = output.IndexOf("Water the plants next.", StringComparison.Ordinal);
        Assert.True(forked < answer && answer < next, output);
        Assert.Contains("› what next?", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }
}
