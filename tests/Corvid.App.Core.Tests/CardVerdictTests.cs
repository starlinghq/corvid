using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>A card's verdict words for calls that asked nobody (G-2), as the engine's assessment fills them.</summary>
public sealed class CardVerdictTests
{
    [Fact]
    public void ACallThatNeededAnApproverAndHadNoneSaysSoAndOffersNoCheckpoint()
    {
        var verdict = new CallVerdict(VerdictKind.NoApprover) { Detail = "manual mode asks before every side effect", CheckpointId = "c-1" };

        Assert.Equal("denied: nobody to ask", ToolCardBlock.VerdictShort(verdict));
        Assert.Equal("needed an approval and nobody could answer: manual mode asks before every side effect", ToolCardBlock.WhyText(verdict));
    }

    [Fact]
    public void ACallTheModeRanUnaskedNamesTheModeWhatItDoesTheRiskAndTheCheckpoint()
    {
        var verdict = new CallVerdict(VerdictKind.Mode)
        {
            Mode = ApprovalMode.Permissive, RiskLevel = 0, RiskConfidence = 0.97, Detail = "in workspace, no network", CheckpointId = "c-44",
        };

        Assert.Equal("auto: permissive mode", ToolCardBlock.VerdictShort(verdict));
        Assert.Equal("ran unasked in permissive mode, in workspace, no network · risk 0 (0.97) · checkpoint c-44 taken before the call",
            ToolCardBlock.WhyText(verdict));
    }
}
