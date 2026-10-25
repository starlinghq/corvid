using Corvid.App.Core.Composition;
using Corvid.App.Core.Settings;
using Corvid.App.Engine.Settings;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Settings: the real corvid.json, typed and raw, through the host's config writer (src/Corvid.App.Engine/Settings/).</summary>
    public override ISettingsService CreateSettingsService(AppComposition app) => new EngineSettingsService(Engine);
}
