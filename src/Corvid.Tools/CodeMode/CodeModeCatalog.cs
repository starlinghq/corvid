using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Corvid.Tools.CodeMode;

/// <summary>A7: one tool as code mode's client generators read it.</summary>
/// <param name="Name">The tool's name: an identifier (<see cref="CodeModeCatalog.IsIdentifier"/>), sent back in requests.</param>
/// <param name="Description">The tool's description. Untrusted text (an MCP server wrote it): a generator escapes it.</param>
/// <param name="Parameters">The arguments' JSON Schema (an object schema), as the tool declares it.</param>
public sealed record CodeModeTool(string Name, string Description, JsonElement Parameters);

/// <summary>
/// A7: the tools a code-mode script may call, and the descriptor JSON its client is generated from. The registry's tools,
/// intersected with the turn's offered tools (<see cref="ToolCallContext.OfferedTools"/>: after a guest's scope, the turn's
/// filters and MCP withholding) and code mode's allowlist. Left out as well: a tool with a secret parameter (its enum of
/// catalogue names is filled per turn, not in the registry's schema) and a name that is not an identifier, which no client
/// could name. The frozen format is in docs/architecture.md, "Code mode wire format (A7)".
/// </summary>
public static partial class CodeModeCatalog
{
    /// <summary>The descriptor format's version, written as <c>"version"</c>.</summary>
    public const int Version = 1;

    /// <summary>
    /// The tools a script may call, ordered by name. <paramref name="offered"/> null (outside a turn) offers nothing. The
    /// allowlist names tools; a name it lists that the registry or the turn lacks is simply absent.
    /// </summary>
    public static IReadOnlyList<CodeModeTool> Describe(ToolRegistry registry, IReadOnlySet<string>? offered, IEnumerable<string> allowlist)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(allowlist);
        if (offered is null)
            return [];
        var allowed = new HashSet<string>(allowlist, StringComparer.Ordinal);
        return
        [
            .. registry.List()
                .Where(tool => allowed.Contains(tool.Name) && offered.Contains(tool.Name) && !tool.HasSecretParameters && IsIdentifier(tool.Name))
                .Select(tool => new CodeModeTool(tool.Name, tool.Description, tool.JsonSchema)),
        ];
    }

    /// <summary>
    /// The descriptor JSON: <c>{"version":1,"tools":[{"name":…,"description":…,"parameters":{…}}]}</c>, compact UTF-8, tools
    /// in the order given.
    /// </summary>
    public static byte[] ToJson(IReadOnlyList<CodeModeTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, CodeModeProtocol.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteStartArray("tools");
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("parameters");
                tool.Parameters.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>A name every client language can use as is: a letter or '_', then letters, digits or '_'; at most 64.</summary>
    public static bool IsIdentifier(string name) => name.Length <= 64 && Identifier().IsMatch(name);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
}
