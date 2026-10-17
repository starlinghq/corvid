using System.Text.RegularExpressions;

namespace Corvid.Vault.Contracts;

/// <summary>
/// Placeholder format: <c>vsec_{name}_{token}</c>, where the name is a secret name and the token is 24 random
/// alphanumerics minted by Vault for one session. A placeholder is meaningless off the machine and outside its session.
/// </summary>
public static partial class Placeholders
{
    public const string Prefix = "vsec_";

    public const int TokenLength = 24;

    /// <summary>True if <paramref name="name"/> is a valid secret name: lower-case letters, digits, dots and dashes, 1 to 64 characters.</summary>
    public static bool IsValidSecretName(string name) => SecretNameRegex().IsMatch(name);

    /// <summary>Every placeholder in <paramref name="text"/>, in order.</summary>
    public static IEnumerable<PlaceholderMatch> Find(string text)
    {
        foreach (Match m in PlaceholderRegex().Matches(text))
        {
            yield return new PlaceholderMatch(m.Value, m.Groups["name"].Value, m.Index, m.Length);
        }
    }

    public static bool Contains(string text) => text.Contains(Prefix, StringComparison.Ordinal) && PlaceholderRegex().IsMatch(text);

    public static string Format(string secretName, string token) => $"{Prefix}{secretName}_{token}";

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9.\-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNameRegex();

    [GeneratedRegex(@"vsec_(?<name>[a-z0-9][a-z0-9.\-]{0,63})_(?<token>[A-Za-z0-9]{24})(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}

/// <param name="Value">The whole placeholder text.</param>
/// <param name="SecretName">The secret it names (not trusted until Vault resolves the token).</param>
public readonly record struct PlaceholderMatch(string Value, string SecretName, int Index, int Length);
