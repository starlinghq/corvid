using System.Collections.Immutable;

namespace Corvid.Kernel.Turns;

/// <summary>
/// A5: asks the person an <c>ask_user</c> question inside the running turn, where a surface that can answer now is attached
/// to the session (the app's window, <c>corvid chat</c>). The turn waits for the answer, as it waits for an approval, then
/// goes on with it as the call's result. Where nobody can answer inside the turn (ACP, a gateway chat, a scheduled run) the
/// turn ends on the question as K-6 does, and the person's next message answers it; so does the decider's ingress
/// <c>clarify</c>, which asks before anything has been done and has nothing to go on with.
/// </summary>
public interface IQuestionAsker
{
    /// <summary>Whether a surface attached to the session can answer a question now; false keeps K-6's end-the-turn path.</summary>
    bool CanAsk(string sessionId);

    /// <summary>
    /// The person's answer, or null when they put the question aside: the turn then ends on it, as K-6 does. The answer is
    /// the person's words, or the visible text of an option they picked (never the option's hidden characters). Cancelled
    /// with the turn (an interrupt, the window closing), which ends the turn as cancelled.
    /// </summary>
    ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken);

    /// <summary>
    /// Called just before the question is logged, and then always followed by <see cref="AskAsync"/> for the same prompt: a
    /// surface that shows the question from the log can be waiting for its answer before it appears, so an answer given as
    /// soon as it shows finds the turn waiting. It runs under the turn's lock: it must not block or call back into the turn.
    /// </summary>
    void Expect(QuestionPrompt prompt)
    {
    }
}

/// <summary>An <c>ask_user</c> question asked inside a turn. The text and options are the model's: untrusted.</summary>
/// <param name="QuestionId">The log's <c>ClarificationAsked</c> id, which its <c>ClarificationAnswered</c> names.</param>
/// <param name="CallId">The <c>ask_user</c> call whose result the answer becomes.</param>
public sealed record QuestionPrompt(string SessionId, string QuestionId, string CallId, string Text, ImmutableArray<string> Options);
