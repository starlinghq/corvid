using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp;

/// <summary>
/// A listed tool's definition in canonical form (C-022): what the model is shown about the tool and what decides how Corvid
/// treats it, as one JSON object (<c>annotations</c>, <c>description</c>, <c>inputSchema</c>, <c>name</c>, <c>title</c>) with
/// every object's keys sorted ordinally and recursively, no whitespace, numbers as the server wrote them and arrays in order.
/// The text is the server's raw text, before <see cref="McpToolMapping"/> cleans it, so a hidden character added to a
/// description is a change; sorted keys mean a server that re-emits the same schema in another order is not.
/// </summary>
/// <param name="Canonical">The canonical JSON, kept with the pin so a change can be shown, not only detected.</param>
/// <param name="Hash">SHA-256 of the canonical JSON's UTF-8 bytes, lower-case hex.</param>
public sealed record McpToolDefinition(string Name, string Canonical, string Hash)
{
    private static readonly JsonWriterOptions Compact = new() { Indented = false };

    public static McpToolDefinition From(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("annotations");
            if (tool.Annotations is { } annotations)
                Write(writer, JsonSerializer.SerializeToElement(annotations, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ToolAnnotations))));
            else
                writer.WriteNullValue();
            WriteText(writer, "description", tool.Description);
            writer.WritePropertyName("inputSchema");
            Write(writer, tool.InputSchema);
            WriteText(writer, "name", tool.Name);
            WriteText(writer, "title", tool.Title);
            writer.WriteEndObject();
        }
        var canonical = Encoding.UTF8.GetString(buffer.WrittenSpan);
        return new McpToolDefinition(tool.Name, canonical, Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan)));
    }

    /// <summary>The canonical JSON indented, for showing a definition or a difference between two.</summary>
    public static string Indent(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        using var document = JsonDocument.Parse(canonical);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            document.RootElement.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>One top-level field of a canonical definition: a string's text, anything else as indented JSON, null when absent.</summary>
    public static string? Field(string canonical, string name)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        using var document = JsonDocument.Parse(canonical);
        if (!document.RootElement.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : Indent(value.GetRawText());
    }

    private static void WriteText(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Ordinal and stable: a key a server repeats keeps its order among equals.
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            default:
                // Numbers keep the server's text (1.0 and 1 differ); true, false and null are their own text.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }
}
