using Corvid.App.Core;
using Corvid.App.Core.Sessions;
using Corvid.Host.Sessions;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// The running turn beyond sending and interrupting (A4): steering (K-3) and clarifying questions (K-6). Turn progress and
/// running tails (K-2) need nothing here: they arrive on the turn's report like every other update.
/// </summary>
public sealed partial class EngineSessionService
{
    /// <summary>K-3: the turn strip offers Ctrl+Enter, which <see cref="SteerAsync"/> takes.</summary>
    public bool CanSteer => true;

    /// <summary>
    /// K-3, Ctrl+Enter: a message for the window's running turn, taken at its next step checkpoint. It shows as a steer chip
    /// in the queue until then; once taken the chip goes and the message shows in the turn as a steer. A steer the turn
    /// ends without taking (it was answering, or had no more steps) becomes an ordinary message at the head of the queue, so
    /// it is the next one sent and never lost. With no turn of the window's running, or one whose message the session has
    /// not begun yet, it is sent as Enter sends it.
    /// </summary>
    public async Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // A4: a ! or # line is not a steer: it goes as Enter sends it (a # line is staged at once, a ! line queues as a turn).
        if (ComposerLine.Parse(text) is not null)
        {
            await QueueAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
            return;
        }
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        QueuedMessage? chip = null;
        Task<bool>? taken = null;
        lock (channel.Gate)
        {
            // The steer goes to the turn the window began: its prompt is in the log, so the session's runner takes steers.
            if (!channel.Closed && channel.Running && channel.TurnBegun && channel.Reporter is not null
                && runtime.TryGetSession(sessionId, out var session))
            {
                chip = new QueuedMessage($"q-{++channel.QueueCounter}", text.Trim()) { Steer = true };
                var withdraw = new CancellationTokenSource();
                // Under the channel's lock, so the turn's end (which settles steers under it) sees this one or none of it.
                taken = session.SteerAsync(chip.Text, withdraw.Token);
                if (taken is { IsCompletedSuccessfully: true, Result: false })
                {
                    // The turn takes no steers (a ! line's runs its command with no step checkpoint) or has just ended:
                    // the message goes as Enter sends it, never as a steer chip waiting on nothing.
                    withdraw.Dispose();
                    (chip, taken) = (null, null);
                }
                else
                {
                    channel.Queue = channel.Queue.Add(chip);
                    channel.Steers[chip.Id] = new PendingSteer(taken, withdraw);
                }
            }
        }
        if (chip is null || taken is null)
        {
            await QueueAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
            return;
        }
        PublishQueue(channel);
        Track(SettleWhenAnsweredAsync(channel, taken));
    }

    private async Task SettleWhenAnsweredAsync(Channel channel, Task<bool> taken)
    {
        await ((Task)taken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        SettleSteers(channel);
    }

    /// <summary>
    /// The steer chips whose turn has answered: a taken one leaves the queue (its message is in the transcript), one not
    /// taken becomes an ordinary queued message at the head, in order. Chips still waiting stay. Idempotent.
    /// </summary>
    private void SettleSteers(Channel channel)
    {
        var changed = false;
        lock (channel.Gate)
        {
            var back = new List<QueuedMessage>();
            var queue = channel.Queue;
            foreach (var item in channel.Queue)
            {
                if (!item.Steer || !channel.Steers.TryGetValue(item.Id, out var pending) || !pending.Taken.IsCompleted)
                    continue;
                channel.Steers.Remove(item.Id);
                pending.Withdraw.Dispose();
                queue = queue.Remove(item);
                if (!pending.Taken.IsCompletedSuccessfully || !pending.Taken.Result)
                    back.Add(item with { Steer = false });
                changed = true;
            }
            if (changed)
                channel.Queue = queue.InsertRange(0, back);
        }
        if (changed)
            PublishQueue(channel);
    }

    /// <summary>Waits for the turn's answer on every steer chip (they all have one once its delivery is in), then settles them.</summary>
    private async Task SettleSteersAsync(Channel channel)
    {
        Task[] waiting;
        lock (channel.Gate)
            waiting = [.. channel.Steers.Values.Select(pending => pending.Taken)];
        if (waiting.Length > 0)
            await Task.WhenAll(waiting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        SettleSteers(channel);
    }

    /// <summary>
    /// Takes a steer chip back from the turn (remove or take back): withdrawn while it still waited, or already taken (true,
    /// and its message is in the transcript). Cancelling completes a waiting steer as not taken at once; one a checkpoint has
    /// begun taking is waited for, since its logging can still fail (the kernel then answers it not taken, at the turn's end
    /// at the latest), so a steer is counted delivered only once the turn says it took it. False when the id is no steer.
    /// </summary>
    private static async Task<bool> WithdrawSteerAsync(Channel channel, string queuedId)
    {
        PendingSteer? pending;
        lock (channel.Gate)
            if (!channel.Steers.Remove(queuedId, out pending))
                return false;
        pending.Withdraw.Cancel();
        await ((Task)pending.Taken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        pending.Withdraw.Dispose();
        return pending.Taken is { IsCompletedSuccessfully: true, Result: true };
    }

    /// <summary>Nit 7: steer entries whose chip has left the queue some other way; under the channel's lock.</summary>
    private static void DropStaleSteers(Channel channel)
    {
        foreach (var id in channel.Steers.Keys.ToList())
        {
            if (channel.Queue.Any(item => item.Id == id))
                continue;
            channel.Steers.Remove(id, out var stale);
            stale!.Withdraw.Cancel();
            stale.Withdraw.Dispose();
        }
    }

    /// <summary>Tests only: the steers this window still waits on for the session.</summary>
    internal int PendingSteers(string sessionId)
    {
        if (!_channels.TryGetValue(sessionId, out var channel))
            return 0;
        lock (channel.Gate)
            return channel.Steers.Count;
    }

    /// <summary>
    /// K-6: answers the open clarifying question with an option or the person's words, sent as their next message (the
    /// question shows as answered just before it). The answer goes ahead of anything queued: messages queued while the
    /// question was open wait behind it, so nothing the person typed before seeing it answers it. A question that has
    /// closed (answered from another view) or another question's id is refused, and nothing is sent.
    /// </summary>
    public async Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        var offered = false;
        lock (channel.Gate)
        {
            if (channel.View?.Question is not { } open || open.QuestionId != questionId)
                throw new InvalidOperationException("That question is no longer open; nothing was sent.");
            if (channel.Running)
                throw new InvalidOperationException("A turn is running; answer the question once it ends.");
            // The model wrote the options: one passed as it was offered, hidden characters and all, is sent only as the text
            // a reader sees, so nothing the person did not see goes to the generator as their words (or clears the
            // session's taint). The view models already send the visible text; this holds for any caller.
            offered = open.Options.Contains(answer, StringComparer.Ordinal);
            if (SafeText.HasHidden(answer) && offered)
            {
                answer = SafeText.Clean(answer).Trim();
                if (answer.Length == 0)
                    throw new InvalidOperationException("That option is only hidden characters; answer in your own words. Nothing was sent.");
            }
        }
        // M1: the person's own words that read as a composer mode line are that line, not an answer: a # line stages its fact
        // at once and the question stays open; a ! line is refused (its command would run as the answer), and the caller keeps
        // the text for after the answer.
        if (!offered)
        {
            switch (ComposerLine.Parse(answer))
            {
                case ComposerLine.Memory memory:
                    await RememberAsync(sessionId, memory.Text, cancellationToken).ConfigureAwait(false);
                    return;
                case ComposerLine.Shell:
                    throw new InvalidOperationException("A question is open: answer it first, then send the ! line. Nothing was sent.");
                case ComposerLine.Empty empty:
                    throw new InvalidOperationException(empty.Message);
            }
        }
        // An option is the model's word, sent as it was offered: never read as a ! or # composer line.
        await EnqueueAsync(sessionId, answer, modeLines: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A steer chip's answer from the turn, and how to take it back while it waits.</summary>
    private sealed record PendingSteer(Task<bool> Taken, CancellationTokenSource Withdraw);
}
