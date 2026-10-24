using Corvid.App.Core.Secrets;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SecretsViewModel? _secrets;

    /// <summary>The Secrets area: what Vault says about its secrets.</summary>
    public SecretsViewModel Secrets => _secrets ??= new SecretsViewModel();
}
