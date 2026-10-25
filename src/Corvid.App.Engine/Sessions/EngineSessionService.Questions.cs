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

    /// <summary>Tests only: the question this window's running turn waits on, or null.</summary>
    internal string? QuestionWaiting(string sessionId)
    {
        if (!_channels.TryGetValue(sessionId, out var channel))
            return null;
        lock (channel.Gate)
            return channel.AskedInTurn?.QuestionId;
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

        public async ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
        {
            // Registered before it shows, so an answer given as soon as it appears finds the turn waiting for it.
            var waiting = new InTurnQuestion(prompt.QuestionId, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (channel.Gate)
            {
                if (channel.Cancelling || service._disposed)
                    return null;
                channel.AskedInTurn = waiting;
            }
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
