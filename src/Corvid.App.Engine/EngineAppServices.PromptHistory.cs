using Corvid.App.Core.Composition;
using Corvid.App.Core.Palette;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>PromptHistory: refused until A3 integration builds it (src/Corvid.App.Engine/PromptHistory/).</summary>
    public override IPromptHistory CreatePromptHistory(AppComposition app) =>
        new Unavailable.PromptHistory(Unavailable.ComesWith("integration", "prompt history across sessions"));
}
