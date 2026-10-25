using Corvid.App.Core.Composition;
using Corvid.App.Core.Skills;
using Corvid.App.Engine.Skills;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Skills over the runtime's store, learning loop and curator (src/Corvid.App.Engine/Skills/).</summary>
    public override ISkillsService CreateSkillsService(AppComposition app) => new EngineSkillsService(Engine, app.Time, Hermes);
}
