using System.Text.Json;

namespace Corvid.Tools;

/// <summary>Risk metadata declared on a tool with <see cref="SideEffectAttribute"/>, <see cref="NetworkAttribute"/> and <see cref="DestructiveAttribute"/>.</summary>
/// <param name="Probe">
/// Declared with <see cref="ProbeAttribute"/>: the tool may be tried with a substitute argument and the result discarded.
/// Never inferred from <see cref="ReadOnly"/>, which can be a claim (an MCP server's annotation, a user's configuration).
/// </param>
/// <param name="OutsideDescription">
/// The tool's description and schema text came from outside the user (an untrusted MCP server's listing, C-022): a turn that
/// shows them to the generator is tainted as an untrusted result would taint it (plan §10.3). Never set by the generator.
/// </param>
public sealed record ToolRisk(SideEffects SideEffects, bool Network, bool Destructive, bool Probe = false, bool OutsideDescription = false)
{
    /// <summary>Changes nothing outside the process. Network reads still count as read-only; taint applies separately.</summary>
    public bool ReadOnly => SideEffects == SideEffects.None;
}

/// <param name="Name">The JSON property name, which is the C# parameter name.</param>
/// <param name="Required">True when the method gives the parameter no default value.</param>
/// <param name="IsSecret">A <see cref="Corvid.Vault.Client.SecretRef"/>: rendered as an enum of catalogue names (§10.5).</param>
public sealed record ToolParameter(string Name, string? Description, bool Required, bool IsSecret);

/// <summary>Everything known about a tool without invoking it. Built at compile time by the generator.</summary>
public sealed class ToolDescriptor
{
    public ToolDescriptor(string name, string description, string jsonSchema, ToolRisk risk, IReadOnlyList<ToolParameter> parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(risk);
        ArgumentNullException.ThrowIfNull(parameters);
        Name = name;
        Description = description;
        // The generator writes the schema text; parsing a document is the only runtime work, and needs no reflection.
        using (JsonDocument document = JsonDocument.Parse(jsonSchema))
        {
            JsonSchema = document.RootElement.Clone();
        }

        Risk = risk;
        Parameters = parameters;
        HasSecretParameters = parameters.Any(p => p.IsSecret);
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>The parameter schema as emitted at compile time. Secret parameters carry no enum here; see <see cref="ToolRegistry.GetFunctions"/>.</summary>
    public JsonElement JsonSchema { get; }

    public ToolRisk Risk { get; }

    public IReadOnlyList<ToolParameter> Parameters { get; }

    public bool HasSecretParameters { get; }

    /// <summary>Where the tool came from when it is not built in: <c>mcp:&lt;server&gt;</c> for an MCP server's tool (C-022).</summary>
    public string? Origin { get; init; }

    public override string ToString() => Name;
}
