using System.Collections.Immutable;
using System.Threading.Channels;
using Corvid.Approvals;
using Corvid.Cli.Chat;
using Spectre.Console.Testing;

namespace Corvid.Cli.Tests;

/// <summary>M4 and security L5 of the A5 review: the chat's approval prompts, one card at a time, and a cut goal read whole.</summary>
public sealed class CliApprovalPrompterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Lines the test types, each read once by a fresh read; it records when each read began.</summary>
    private sealed class TypedInput : IChatInput
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>();

        public Channel<int> Reads { get; } = Channel.CreateUnbounded<int>();

        private int _reads;

        public bool Echoes => false;

        public void Type(string line) => _lines.Writer.TryWrite(line);

        public async ValueTask<string?> ReadLineAsync(bool fresh = false, CancellationToken cancellationToken = default)
        {
            Reads.Writer.TryWrite(Interlocked.Increment(ref _reads));
            return await _lines.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask<string?> ReadDuringTurnAsync(Func<string, bool> accepts, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
    }

    private static ApprovalPrompt Prompt(string id, string text) =>
        new(id, "cli-1", new ActionAnalysis("tool", text, [], null), [], [ApprovalScope.Once], DateTimeOffset.UtcNow.AddMinutes(5));

    [Fact]
    public async Task M4_parallel_prompts_are_shown_and_answered_one_at_a_time()
    {
        var console = new TestConsole();
        var input = new TypedInput();
        var prompter = new CliApprovalPrompter(new ChatRenderer(console), input);

        var first = prompter.AskAsync(Prompt("a-1", "first child's call"), Ct).AsTask();
        var second = prompter.AskAsync(Prompt("a-2", "second child's call"), Ct).AsTask();
        Assert.Equal(1, await input.Reads.Reader.ReadAsync(Ct));

        // One card is up and read; the other waits unseen, so the y typed now answers the card above it.
        var shown = console.Output;
        Assert.True(shown.Contains("first child's call", StringComparison.Ordinal) ^ shown.Contains("second child's call", StringComparison.Ordinal), shown);
        var firstShown = shown.Contains("first child's call", StringComparison.Ordinal);
        input.Type("y");
        Assert.Equal(2, await input.Reads.Reader.ReadAsync(Ct));
        Assert.Contains(firstShown ? "second child's call" : "first child's call", console.Output, StringComparison.Ordinal);
        input.Type("n");

        var (approved, denied) = firstShown ? (await first, await second) : (await second, await first);
        Assert.True(approved.Approved);
        Assert.False(denied.Approved);
    }

    [Fact]
    public async Task Security_L5_a_cut_goal_is_read_whole_with_g()
    {
        var console = new TestConsole();
        var input = new TypedInput();
        var prompter = new CliApprovalPrompter(new ChatRenderer(console), input, (_, whole, _) =>
            Task.FromResult<IReadOnlyList<string>>(whole ? ["Child: the whole goal, every word of it"] : ["Child: the whole… (21 more characters)"]));

        var answer = prompter.AskAsync(Prompt("a-1", "delegate_task"), Ct).AsTask();
        await input.Reads.Reader.ReadAsync(Ct);
        Assert.Contains("answer g to read it whole", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("every word of it", console.Output, StringComparison.Ordinal);
        input.Type("g");
        await input.Reads.Reader.ReadAsync(Ct);
        Assert.Contains("every word of it", console.Output, StringComparison.Ordinal);
        input.Type("y");

        Assert.True((await answer).Approved);
    }
}
