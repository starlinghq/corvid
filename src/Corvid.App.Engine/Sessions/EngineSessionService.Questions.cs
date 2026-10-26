using Corvid.Host.Sessions;
using Corvid.Kernel.Turns;

namespace Corvid.App.Engine.Sessions;

public sealed partial class EngineSessionService
{
    /// <summary>
    /// A5: a question this window's turn asked inside the turn (<c>ask_user</c>), waiting for the person: its id, and where
    /// <see cref="AnswerQuestionAsync"/> puts the answer.
    /// </summary>
    internal sealed record InTurnQuestion(string QuestionId, TaskCompletionSource<string?> Answer);

    /// <summary>
    /// Tests only: false takes K-6's end-the-turn path for this window's turns too, as a turn the window did not start (a
    /// job's, the gateway's) and the decider's ingress clarify still do, so those paths stay tested.
    /// </summary>
    internal bool QuestionsInTurn { get; set; } = true;

    /// <summary>Tests only: awaited as the asker is asked, before it looks at the window (an interrupt's race).</summary>
    internal Func<Task>? AskEntering { get; set; }

    /// <summary>Tests only: called once the asker has taken the question as waiting (it then waits for the answer).</summary>
    internal Action? AskWaiting { get; set; }

    /// <summary>Tests only: the question this window's running turn waits on, or null.</summary>
    internal string? QuestionWaiting(string sessionId)
    {
        if (!_channels.TryGetValue(sessionId, out var channel))
            return null;
        lock (channel.Gate)
            return (channel.AskedInTurn ?? Volatile.Read(ref channel.Expected))?.QuestionId;
    }

    /// <summary>
    /// A5: answers the turn's <c>ask_user</c> question inside the turn, while this window runs it: the question shows as
    /// every question does (its block, the tray, the rail), the turn waits, and the composer's or a block's answer
    /// (<see cref="AnswerQuestionAsync"/>) is the call's result. An interrupt cancels the wait with the turn.
    /// </summary>
    private sealed class WindowQuestioner(EngineSessionService service, Channel channel, SessionUpdateReporter reporter) : IQuestionAsker
    {
        public bool CanAsk(string sessionId)
        {
            if (service._disposed || !service.QuestionsInTurn)
                return false;
            lock (channel.Gate)
                return !channel.Closed && !channel.ReadOnly && !channel.Cancelling && ReferenceEquals(channel.Reporter, reporter);
        }

        // Registered before the question is logged (the kernel calls this under its lock, just before), so an answer given as
        // soon as the question shows finds the turn waiting for it. A plain reference write: no lock taken under the turn's.
        public void Expect(QuestionPrompt prompt) =>
            Volatile.Write(ref channel.Expected, new InTurnQuestion(prompt.QuestionId,
                new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously)));

        public async ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
        {
            if (service.AskEntering is { } entering)
                await entering().ConfigureAwait(false);
            InTurnQuestion waiting;
            lock (channel.Gate)
            {
                var expected = Interlocked.Exchange(ref channel.Expected, null);
                waiting = expected is not null && expected.QuestionId == prompt.QuestionId ? expected
                    : new InTurnQuestion(prompt.QuestionId, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
                if (service._disposed)
                {
                    if (ReferenceEquals(channel.AskedInTurn, waiting))
                        channel.AskedInTurn = null;
                    return null;
                }
                // An interrupt under way (it sets Cancelling, then cancels the turn's token) is not answered with null, which
                // would leave the question for the next message: the wait below ends with the token, and the kernel withdraws
                // the question with the turn. AnswerQuestionAsync refuses an answer meanwhile.
                channel.AskedInTurn = waiting;
            }
            service.AskWaiting?.Invoke();
            try
            {
                // The question is in the log: shown (and everything the turn reported before it) while the turn waits.
                await reporter.CatchUpAsync().ConfigureAwait(false);
                using (cancellationToken.Register(() => waiting.Answer.TrySetCanceled(cancellationToken)))
                    return await waiting.Answer.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (channel.Gate)
                    if (ReferenceEquals(channel.AskedInTurn, waiting))
                        channel.AskedInTurn = null;
            }
        }
    }

    /// <summary>The approval prompter and the question asker a turn attaches, let go of together.</summary>
    private sealed class Attached(IDisposable prompter, IDisposable questioner) : IDisposable
    {
        public void Dispose()
        {
            questioner.Dispose();
            prompter.Dispose();
        }
    }
}
