using System.Collections.Concurrent;
using Corvid.Cli.Chat;

namespace Corvid.Cli.Tests;

/// <summary>
/// G-15 and A5: the terminal's input read during a turn. A line goes to an approval prompt first, then an ordinary read, then
/// the turn's reader when it takes the line (<c>/fork</c>, <c>/model</c>), so reading beside a turn can never take a prompt's
/// answer, and a line it does not take stays typed ahead for the next ordinary read.
/// </summary>
public sealed class ChatInputTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private static bool Fork(string line) => line == "/fork";

    [Fact]
    public async Task A_prompt_takes_a_line_before_the_turns_reader_and_a_line_it_does_not_take_is_the_next_ordinary_read()
    {
        using var typed = new BlockingCollection<string?>();
        var input = new ConsoleChatInput(() => typed.Take(), echoes: false);

        var during = input.ReadDuringTurnAsync(Fork, Ct).AsTask();
        var prompt = input.ReadLineAsync(fresh: true, Ct).AsTask();
        typed.Add("y", Ct);

        Assert.Equal("y", await prompt.WaitAsync(Bound, Ct));
        Assert.False(during.IsCompleted);
        // A message typed during the turn is not the turn reader's: it waits, typed ahead, and /fork after it is taken now.
        typed.Add("what next?", Ct);
        typed.Add("/fork", Ct);
        Assert.Equal("/fork", await during.WaitAsync(Bound, Ct));
        Assert.Equal("what next?", await input.ReadLineAsync(cancellationToken: Ct).AsTask().WaitAsync(Bound, Ct));

        // The turn's reader is cancelled when the turn ends; the next line goes to whoever reads next.
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = input.ReadDuringTurnAsync(Fork, ended.Token).AsTask();
        await ended.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var ordinary = input.ReadLineAsync(cancellationToken: Ct).AsTask();
        typed.Add("hello", Ct);
        Assert.Equal("hello", await ordinary.WaitAsync(Bound, Ct));

        // The end of input ends every read.
        var last = input.ReadDuringTurnAsync(Fork, Ct).AsTask();
        typed.Add(null, Ct);
        Assert.Null(await last.WaitAsync(Bound, Ct));
        Assert.Null(await input.ReadLineAsync(cancellationToken: Ct));
    }
}
