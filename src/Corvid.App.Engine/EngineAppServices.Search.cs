using Corvid.App.Core.Composition;
using Corvid.App.Core.Palette;
using Corvid.App.Engine.PromptHistory;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>G-5: search across sessions through the runtime's episodic index (src/Corvid.App.Engine/PromptHistory/).</summary>
    public override ISessionSearch? CreateSessionSearch(AppComposition app) => new EngineSessionSearch(Engine);
}
