using Corvid.App.Core.Secrets;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private ISecretsService? _secretsService;
    private SecretsViewModel? _secrets;

    /// <summary>
    /// What Vault will say about its secrets, and the features configured to use them. Demo mode (the engine's comes from
    /// <see cref="Services"/> instead) starts nothing and writes nothing.
    /// </summary>
    public ISecretsService SecretsService => _secretsService ??= Services.CreateSecretsService(this);

    /// <summary>The Secrets area: names, policy and what waits on which name. Never a value.</summary>
    public SecretsViewModel Secrets => _secrets ??= new SecretsViewModel(SecretsService, Time);

    private void DisposeSecrets()
    {
        _secrets?.Dispose();
        (_secretsService as IDisposable)?.Dispose();
    }
}
