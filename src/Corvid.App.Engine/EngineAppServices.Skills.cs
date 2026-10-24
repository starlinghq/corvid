using Corvid.App.Core.Composition;
using Corvid.App.Core.Skills;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Skills: refused until A3 knowledge builds it (src/Corvid.App.Engine/Skills/).</summary>
    public override ISkillsService CreateSkillsService(AppComposition app) =>
        new Unavailable.Skills(Unavailable.ComesWith("knowledge", "the skills store"));
}
