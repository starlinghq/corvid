using Corvid.App.Core.Composition;
using Corvid.App.Core.Secrets;

namespace Corvid.App.Engine;

public sealed partial class EngineAppServices
{
    /// <summary>Secrets: refused until A3 ops builds it (src/Corvid.App.Engine/Secrets/).</summary>
    public override ISecretsService CreateSecretsService(AppComposition app) =>
        new Unavailable.Secrets(Unavailable.ComesWith("ops", "the Vault catalogue"));
}
