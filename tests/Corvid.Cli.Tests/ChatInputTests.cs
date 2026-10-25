using System.Collections.Concurrent;
using Corvid.Cli.Chat;

namespace Corvid.Cli.Tests;

/// <summary>
/// G-15: the terminal's input read during a turn. A line goes to an approval prompt first, then an ordinary read, then the
/// turn's reader, so reading beside a turn (for <c>/fork</c>) can never take a prompt's answer.
/// </summary>
public sealed class ChatInputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_prompt_takes_a_line_before_the_turns_reader_and_a_line_given_back_is_the_next_ordinary_read()
    {
        using var typed = new BlockingCollection<string?>();
        var input = new ConsoleChatInput(() => typed.Take(), echoes: false);

        var during = input.ReadDuringTurnAsync(Ct).AsTask();
        var prompt = input.ReadLineAsync(fresh: true, Ct).AsTask();
        typed.Add("y", Ct);

        Assert.Equal("y", await prompt.WaitAsync(Bound, Ct));
        Assert.False(during.IsCompleted);
        typed.Add("/fork", Ct);
        Assert.Equal("/fork", await during.WaitAsync(Bound, Ct));

        // A line that is not a command beside the turn goes back, and the next ordinary read returns it.
        input.PutBack("what next?");
        Assert.Equal("what next?", await input.ReadLineAsync(cancellationToken: Ct).AsTask().WaitAsync(Bound, Ct));

        // The turn's reader is cancelled when the turn ends; the next line goes to whoever reads next.
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = input.ReadDuringTurnAsync(ended.Token).AsTask();
        await ended.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var ordinary = input.ReadLineAsync(cancellationToken: Ct).AsTask();
        typed.Add("hello", Ct);
        Assert.Equal("hello", await ordinary.WaitAsync(Bound, Ct));

        // The end of input ends every read.
        var last = input.ReadDuringTurnAsync(Ct).AsTask();
        typed.Add(null, Ct);
        Assert.Null(await last.WaitAsync(Bound, Ct));
        Assert.Null(await input.ReadLineAsync(cancellationToken: Ct));
    }
}
