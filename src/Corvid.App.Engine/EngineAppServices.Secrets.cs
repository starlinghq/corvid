using Corvid.App.Core.Composition;
using Corvid.App.Core.Secrets;
using Corvid.App.Engine.Secrets;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Secrets over Vault's catalogue and audit summary and the configured features (src/Corvid.App.Engine/Secrets/).</summary>
    public override ISecretsService CreateSecretsService(AppComposition app) => new EngineSecretsService(Engine, app.Dispatcher, app.Time);
}
