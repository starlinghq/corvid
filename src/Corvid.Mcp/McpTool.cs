using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp;

/// <summary>
/// One tool of a configured MCP server as a Corvid tool (plan §7: MCP tools get the same treatment as built-in ones). It is a
/// <see cref="ToolFunction"/>, so the registry offers it, the shortlist embeds it, the turn loop validates its arguments, and
/// its <see cref="ToolRisk"/> decides whether the approval pipeline asks. The call goes to the server's live connection,
/// which reconnects lazily, so the tool stays registered while the server restarts.
/// </summary>
public sealed class McpTool : ToolFunction
{
    private readonly McpServerConnection _server;

    internal McpTool(McpServerConnection server, string remoteName, ToolDescriptor descriptor)
        : base(descriptor)
    {
        _server = server;
        RemoteName = remoteName;
    }

    /// <summary>The server that serves the tool.</summary>
    public string Server => _server.Definition.Name;

    /// <summary>The tool's name on its server; <see cref="AIFunction.Name"/> is the prefixed name the model calls.</summary>
    public string RemoteName { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var context = ToolCallContext.From(arguments);
        // Plan §10.3: whatever an untrusted server returns (a result or an error message) is outside content. Marked before the
        // call, so an error text that reaches the model taints the session too.
        // A flagged tool the user allowed (C-022) is treated as an untrusted server's whatever the server's trust.
        if (Outside)
            context?.MarkResultTainted();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in arguments)
            values[name] = McpResults.ToElement(value);
        // Outside a turn nobody knows what the session holds; Vault is told the worst.
        var tainted = context?.Tainted ?? true;
        var result = await _server.CallAsync(RemoteName, values, tainted, cancellationToken).ConfigureAwait(false);
        var shaped = McpResults.Shape(result, _server.Definition.MaxResultChars);
        if (result.IsError == true)
            throw new McpToolException($"{_server.Definition.Name}/{RemoteName} reported an error: {shaped.Text}");
        var output = new JsonObject
        {
            ["server"] = _server.Definition.Name,
            ["content"] = shaped.Text,
        };
        if (shaped.Structured is not null)
            output["structured"] = shaped.Structured;
        if (shaped.Truncated)
            output["truncated"] = true;
        if (Outside || Descriptor.Risk.Network)
            output["tainted"] = true;
        return output;
    }

    private bool Outside => _server.Definition.Trust == McpTrust.Untrusted || Descriptor.Risk.OutsideDescription;
}

/// <summary>The server ran the tool and said it failed; the message is the server's own text.</summary>
public sealed class McpToolException(string message) : InvalidOperationException(message)
{
    public McpToolException()
        : this("The MCP tool failed.")
    {
    }

    public McpToolException(string message, Exception innerException)
        : this(message + " " + innerException.Message)
    {
    }
}

/// <summary>Turns a server's tool listing into Corvid tool descriptors: names, schemas and risk.</summary>
public static class McpToolMapping
{
    /// <summary>Descriptions reach every prompt that offers the tool, so they are capped like a skill's (C-017).</summary>
    public const int MaxDescriptionChars = 1_024;

    /// <summary>A schema larger than this is refused: it would crowd the prompt and is more likely a mistake than a tool.</summary>
    public const int MaxSchemaChars = 16_384;

    private const int MaxNameLength = 64;

    /// <summary>
    /// The name the model calls: <c>server__tool</c>, characters outside <c>[A-Za-z0-9_-]</c> replaced by '_', and a name
    /// over 64 characters cut with a short hash of the full name so two long names stay distinct.
    /// </summary>
    public static string ToolName(string server, string tool)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentException.ThrowIfNullOrEmpty(tool);
        var builder = new StringBuilder(server.Length + tool.Length + 2).Append(server).Append("__");
        foreach (var c in tool)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        var name = builder.ToString();
        if (name.Length <= MaxNameLength)
            return name;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(server + "\0" + tool)))[..8];
        return name[..(MaxNameLength - 9)] + "_" + hash;
    }

    /// <summary>
    /// The risk Corvid assumes for a tool (plan §10.1: MCP tools with side-effect metadata go through approval). For an
    /// untrusted server annotations are ignored, since a claim of read-only would let a tool run unasked: every tool is a
    /// destructive remote side effect unless the user listed it as read-only. A trusted server's hints are honoured, with
    /// the MCP defaults when a hint is absent (not read-only, destructive, open world). <c>Network</c> is set when what the
    /// tool is sent can leave the machine, so a tainted session asks before any call that could carry its data out, and the
    /// turn loop taints the session with the result. An untrusted server's description is outside content as well
    /// (<see cref="ToolRisk.OutsideDescription"/>, C-022): a turn that shows it to the generator is tainted.
    /// </summary>
    /// <param name="untrusted">
    /// Treat the tool as an untrusted server's whatever the configured trust, ignoring the user's read-only declaration too:
    /// a tool whose description was flagged as poisoned and that the user allowed anyway (C-022).
    /// </param>
    public static ToolRisk Risk(McpServerDefinition server, Tool tool, bool untrusted = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        var outside = untrusted || server.Trust == McpTrust.Untrusted;
        var declaredReadOnly = !untrusted && server.ReadOnlyTools.Contains(tool.Name, StringComparer.Ordinal);
        var hints = outside ? null : tool.Annotations;
        var readOnly = declaredReadOnly || hints?.ReadOnlyHint == true;
        var effects = readOnly ? SideEffects.None : SideEffects.Remote;
        var destructive = !readOnly && (hints?.DestructiveHint ?? true);
        var network = server.ReachesNetwork && (outside || (hints?.OpenWorldHint ?? true));
        return new ToolRisk(effects, network, destructive, OutsideDescription: outside);
    }

    /// <summary>The descriptor for one listed tool, or the reason it is left out.</summary>
    /// <param name="untrusted">See <see cref="Risk"/>.</param>
    public static (ToolDescriptor? Descriptor, string? Problem) Describe(McpServerDefinition server, Tool tool, bool untrusted = false)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(tool);
        if (string.IsNullOrWhiteSpace(tool.Name))
            return (null, "a tool without a name");
        var name = ToolName(server.Name, tool.Name);
        var (schema, parameters, problem) = Schema(tool.InputSchema);
        if (problem is not null)
            return (null, $"'{tool.Name}': {problem}");
        var description = Clean(string.IsNullOrWhiteSpace(tool.Description) ? tool.Title ?? tool.Name : tool.Description!);
        return (new ToolDescriptor(name, description, schema!, Risk(server, tool, untrusted), parameters) { Origin = "mcp:" + server.Name }, null);
    }

    /// <summary>Control characters (other than newlines and tabs) removed, whitespace trimmed, and the text capped.</summary>
    internal static string Clean(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxDescriptionChars + 1));
        foreach (var c in text)
        {
            if (builder.Length > MaxDescriptionChars)
                break;
            // Bidirectional and zero-width formatting characters can hide text from the user while the model reads it.
            if (char.IsControl(c) && c is not ('\n' or '\t') || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                continue;
            builder.Append(c);
        }
        var cleaned = builder.ToString().Trim();
        return cleaned.Length > MaxDescriptionChars ? cleaned[..(MaxDescriptionChars - 1)] + "…" : cleaned;
    }

    private static (string? Schema, ImmutableArray<ToolParameter> Parameters, string? Problem) Schema(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return ("""{"type":"object","properties":{}}""", [], null);
        if (input.ValueKind != JsonValueKind.Object)
            return (null, [], "its input schema is not a JSON object");
        var text = input.GetRawText();
        if (text.Length > MaxSchemaChars)
            return (null, [], $"its input schema is {text.Length} characters, over the {MaxSchemaChars} allowed");
        if (JsonNode.Parse(text) is not JsonObject schema)
            return (null, [], "its input schema is not a JSON object");
        if (schema["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind != "object")
            return (null, [], $"its input schema describes '{kind}', not an object of arguments");
        schema["type"] = "object";
        if (schema["properties"] is not JsonObject properties)
            schema["properties"] = properties = [];
        var required = schema["required"] is JsonArray list
            ? list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var name) ? name : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : [];
        var parameters = properties.Select(property => new ToolParameter(
            property.Key,
            property.Value is JsonObject definition && definition["description"] is JsonValue description && description.TryGetValue<string>(out var text)
                ? Clean(text)
                : null,
            required.Contains(property.Key),
            IsSecret: false)).ToImmutableArray();
        return (schema.ToJsonString(), parameters, null);
    }
}
