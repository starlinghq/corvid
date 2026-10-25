using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;
using Corvid.App.Engine.Models;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Models: the slots, the catalogue, downloads and placement over the engine (src/Corvid.App.Engine/Models/).</summary>
    public override IModelService CreateModelService(AppComposition app) =>
        new EngineModelService(Engine, app.Dispatcher, app.Time, () => app.Shell.CurrentSession?.Id);
}
