namespace Corvid.App.Core.Secrets;

/// <summary>
/// The Secrets area's seam (docs/plan/ui.md §4.11, F17): what Vault will say about its secrets, which features wait on
/// which name, setting a secret outside the app, and binding a feature to a name. No call takes or returns a value. The
/// demo implements it now; Corvid.App.Engine will over Vault's catalogue and audit summary and corvid.json.
/// </summary>
public interface ISecretsService
{
    /// <summary>Vault's answer changed: a secret appeared, a binding was written.</summary>
    event EventHandler? Changed;

    /// <summary>Asks Vault again: its status, the catalogue, and what each configured feature needs.</summary>
    Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts setting a secret outside the app: the Vault helper (U-6) when it exists, a terminal running
    /// <c>corvid-vault set NAME</c> until then. Returns at once; the secret shows in <see cref="LoadAsync"/> once Vault has it.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a valid secret name.</exception>
    Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds a feature to a secret already in Vault, by name (written to corvid.json). Refused when the secret is not bound
    /// to the feature's host, or its role does not allow the feature.
    /// </summary>
    Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default);
}
