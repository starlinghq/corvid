using Corvid.Approvals;

namespace Corvid.Cli.Chat;

/// <summary>
/// Approvals inline in the chat (plan §12): the prompt is printed into the scrollback and answered on the next line. Only a
/// line typed after the prompt appeared counts, and an unanswered prompt is denied at its deadline.
/// <para>
/// M4 (A5 review): one prompt at a time. Parallel children (one <c>delegate_task</c> call's) ask at once; each card is shown
/// and answered before the next is shown, so a <c>y</c> always answers the card printed just above it. A prompt waiting its
/// turn is still denied at its own deadline.
/// </para>
/// </summary>
/// <param name="details">
/// A5: lines the card adds (a <c>delegate_task</c> spawn's children, or which child asks), from the host's
/// <c>DelegationCard</c>; null adds none. The bool asks for every goal in full (security L5) rather than cut for the card.
/// </param>
internal sealed class CliApprovalPrompter(ChatRenderer renderer, IChatInput input,
    Func<ApprovalPrompt, bool, CancellationToken, Task<IReadOnlyList<string>>>? details = null) : IApprovalPrompter, IDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        var wait = prompt.Deadline - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return ApprovalAnswer.Deny();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(wait);
        try
        {
            await _one.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Its deadline came while another card was being answered: never shown, denied.
            return ApprovalAnswer.Deny();
        }
        try
        {
            return await ShowAndReadAsync(prompt, deadline.Token, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<ApprovalAnswer> ShowAndReadAsync(ApprovalPrompt prompt, CancellationToken deadline, CancellationToken cancellationToken)
    {
        var extra = details is null ? [] : await details(prompt, false, deadline).ConfigureAwait(false);
        var cut = Corvid.Host.Composition.DelegationCard.IsCut(extra);
        renderer.ShowApproval(prompt, extra);
        if (cut)
            renderer.ApprovalAnswered("A goal is cut to fit the card; answer g to read it whole.");
        while (true)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(fresh: true, deadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                renderer.ApprovalAnswered("No answer before the deadline: denied.");
                return ApprovalAnswer.Deny();
            }
            // Security L5 (A5 review): the person can read all of what they grant before answering.
            if (cut && details is not null && line?.Trim().ToLowerInvariant() is "g" or "goal")
            {
                renderer.ShowApproval(prompt, await details(prompt, true, deadline).ConfigureAwait(false));
                continue;
            }
            if (Parse(line, prompt) is { } answer)
            {
                renderer.ApprovalAnswered(answer.Approved ? $"Approved ({answer.Scope.ToString().ToLowerInvariant()})." : "Denied.");
                return answer;
            }
            renderer.ApprovalAnswered("Answer y, n" + (prompt.Offered.Contains(ApprovalScope.Session) ? ", s" : "") +
                (prompt.Offered.Contains(ApprovalScope.Always) ? " or a" : "") + (cut ? " (g shows the whole goal)" : "") + ".");
        }
    }

    public void Dispose() => _one.Dispose();

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
