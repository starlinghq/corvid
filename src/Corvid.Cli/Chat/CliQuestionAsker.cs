using Corvid.Host.Commands;
using Corvid.Host.Sessions;
using Corvid.Kernel.Turns;

namespace Corvid.Cli.Chat;

/// <summary>
/// A5: an <c>ask_user</c> question asked inside the turn, inline in the chat like an approval: printed into the scrollback
/// and answered on the next line typed after it, by a choice's number or in the person's words. The turn waits; Ctrl-C
/// cancels it with the turn. The end of input puts the question aside: the turn ends on it, and the next message answers it.
/// A <c>!</c> or <c>#</c> line, or a command, is not an answer: it is refused and the question asked again.
/// </summary>
internal sealed class CliQuestionAsker(ChatRenderer renderer, IChatInput input) : IQuestionAsker
{
    public bool CanAsk(string sessionId) => true;

    public async ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
    {
        var question = new ClarifyingQuestion(prompt.QuestionId, prompt.Text, prompt.Options);
        renderer.ShowQuestion(question);
        while (true)
        {
            var line = await input.ReadLineAsync(fresh: true, cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;
            if (Refusal(line) is { } refusal)
            {
                renderer.ApprovalAnswered(refusal);
                continue;
            }
            return ChatRenderer.Answer(question, line).Trim();
        }
    }

    /// <summary>Why a line is not taken as the answer, or null for an answer.</summary>
    internal static string? Refusal(string line)
    {
        if (line.Trim().Length == 0)
            return "Answer the question, or pick a choice by its number.";
        if (SlashCommandRegistry.IsCommand(line.Trim()))
            return "Commands wait until the turn ends: answer the question first.";
        return ComposerLine.Parse(line) switch
        {
            ComposerLine.Shell => "A question is open: answer it first, then send the ! line. Nothing was sent.",
            ComposerLine.Memory => "A question is open: answer it first, then send the # line. Nothing was sent.",
            _ => null,
        };
    }
}
