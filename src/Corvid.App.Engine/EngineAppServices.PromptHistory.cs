using Corvid.App.Core.Composition;
using Corvid.App.Core.Palette;
using Corvid.App.Engine.PromptHistory;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>PromptHistory over the event log's user messages across the workspace's sessions (src/Corvid.App.Engine/PromptHistory/).</summary>
    public override IPromptHistory CreatePromptHistory(AppComposition app) => new EnginePromptHistory(Engine);
}
