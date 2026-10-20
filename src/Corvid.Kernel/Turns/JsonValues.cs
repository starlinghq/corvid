using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Turns;

/// <summary>
/// Converts the values a chat client and a tool hand back (<see cref="FunctionCallContent.Arguments"/>, tool return
/// values) into the <see cref="JsonElement"/> the event log stores, without reflection for the common shapes.
/// </summary>
internal static class JsonValues
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonElement ToElement(object? value)
    {
        if (value is JsonElement element)
            return element.Clone();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            Write(writer, value, depth: 0);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static JsonElement Object(IDictionary<string, object?>? values) =>
        ToElement(values ?? new Dictionary<string, object?>());

    /// <summary>Arguments as the tool binder reads them: one <see cref="JsonElement"/> per property.</summary>
    public static AIFunctionArguments ToArguments(JsonElement arguments)
    {
        var result = new AIFunctionArguments();
        if (arguments.ValueKind == JsonValueKind.Object)
            foreach (var property in arguments.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        return result;
    }

    public static Dictionary<string, object?> ToDictionary(JsonElement arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.ValueKind == JsonValueKind.Object)
            foreach (var property in arguments.EnumerateObject())
                result[property.Name] = property.Value.Clone();
        return result;
    }

    /// <summary>A string as the generator reads a result: strings as they are, everything else as JSON.</summary>
    public static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static void Write(Utf8JsonWriter writer, object? value, int depth)
    {
        if (depth > 32)
        {
            writer.WriteStringValue("…");
            return;
        }
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonDocument document:
                document.RootElement.WriteTo(writer);
                break;
            // Before the collection cases: a JsonObject is also an IEnumerable of pairs. Tools may return JsonNode (C-005).
            case System.Text.Json.Nodes.JsonNode node:
                node.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case short or byte or sbyte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                writer.WriteNumberValue(number);
                break;
            case float number when float.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case double number when double.IsFinite(number):
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case Guid id:
                writer.WriteStringValue(id);
                break;
            case DateTimeOffset time:
                writer.WriteStringValue(time);
                break;
            case DateTime time:
                writer.WriteStringValue(time);
                break;
            case Enum or Uri or float or double:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    Write(writer, item, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                    Write(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            default:
                // Other types go through the chat abstractions' own serializer options, which carry source-generated
                // metadata for its content types; a type it cannot describe is recorded by its text.
                if (AIJsonUtilities.DefaultOptions.TryGetTypeInfo(value.GetType(), out var info))
                {
                    try
                    {
                        // Into its own element first, so a failure part way leaves the outer writer untouched.
                        JsonSerializer.SerializeToElement(value, info).WriteTo(writer);
                        break;
                    }
                    catch (Exception exception) when (exception is NotSupportedException or JsonException or InvalidOperationException)
                    {
                    }
                }
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name);
                break;
        }
    }
}
