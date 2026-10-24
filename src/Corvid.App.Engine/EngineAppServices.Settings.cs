using Corvid.App.Core.Composition;
using Corvid.App.Core.Settings;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Settings: refused until A3 setup builds it (src/Corvid.App.Engine/Settings/).</summary>
    public override ISettingsService CreateSettingsService(AppComposition app) =>
        new Unavailable.Settings(Unavailable.ComesWith("setup", "editing corvid.json"));
}
