using Corvid.Vault.Contracts;

namespace Corvid.Vault.Client;

/// <summary>
/// A reference to a secret by name. Tool parameters of this type let a model name a secret from Vault's catalogue;
/// there is no way to put a value in one.
/// </summary>
public readonly record struct SecretRef
{
    public SecretRef(string name)
    {
        if (!Placeholders.IsValidSecretName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid secret name (lower-case letters, digits, '.', '-'; 1 to 64 characters).", nameof(name));
        }

        Name = name;
    }

    public string Name { get; }

    public override string ToString() => Name;
}
