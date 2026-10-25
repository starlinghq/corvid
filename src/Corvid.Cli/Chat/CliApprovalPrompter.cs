using Corvid.Approvals;

namespace Corvid.Cli.Chat;

/// <summary>
/// Approvals inline in the chat (plan §12): the prompt is printed into the scrollback and answered on the next line. Only a
/// line typed after the prompt appeared counts, and an unanswered prompt is denied at its deadline.
/// </summary>
/// <param name="details">
/// A5: lines the card adds (a <c>delegate_task</c> spawn's children, or which child asks), from the host's
/// <c>DelegationCard</c>; null adds none.
/// </param>
internal sealed class CliApprovalPrompter(ChatRenderer renderer, IChatInput input,
    Func<ApprovalPrompt, CancellationToken, Task<IReadOnlyList<string>>>? details = null) : IApprovalPrompter
{
    public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        var wait = prompt.Deadline - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return ApprovalAnswer.Deny();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(wait);
        var extra = details is null ? [] : await details(prompt, deadline.Token).ConfigureAwait(false);
        renderer.ShowApproval(prompt, extra);
        while (true)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(fresh: true, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                renderer.ApprovalAnswered("No answer before the deadline: denied.");
                return ApprovalAnswer.Deny();
            }
            if (Parse(line, prompt) is { } answer)
            {
                renderer.ApprovalAnswered(answer.Approved ? $"Approved ({answer.Scope.ToString().ToLowerInvariant()})." : "Denied.");
                return answer;
            }
            renderer.ApprovalAnswered("Answer y, n" + (prompt.Offered.Contains(ApprovalScope.Session) ? ", s" : "") +
                (prompt.Offered.Contains(ApprovalScope.Always) ? " or a" : "") + ".");
        }
    }

    /// <summary>The end of input denies; an unrecognized answer asks again.</summary>
    internal static ApprovalAnswer? Parse(string? line, ApprovalPrompt prompt)
    {
        if (line is null)
            return ApprovalAnswer.Deny();
        return line.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" => ApprovalAnswer.Approve(),
            "s" or "session" when prompt.Offered.Contains(ApprovalScope.Session) => ApprovalAnswer.Approve(ApprovalScope.Session),
            "a" or "always" when prompt.Offered.Contains(ApprovalScope.Always) => ApprovalAnswer.Approve(ApprovalScope.Always),
            "n" or "no" => ApprovalAnswer.Deny(),
            _ => null,
        };
    }
}
