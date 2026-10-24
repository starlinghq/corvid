using Corvid.Approvals;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// The approval prompter a turn started from the window has until A3 sessions brings the approval card's: it denies every
/// prompt (nothing runs unasked, and nothing is approved on the user's behalf) and says so in the transcript.
/// </summary>
internal sealed class DenyingPrompter(Action<string> say) : IApprovalPrompter
{
    public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        say(prompt.Analysis.Text);
        return ValueTask.FromResult(ApprovalAnswer.Deny());
    }
}
