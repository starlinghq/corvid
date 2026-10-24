using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Abstractions.Events;

namespace Corvid.Kernel.Events;

// Source-generated so the log stays trim and Native AOT safe. The serialized form is the persisted format:
// changing the naming policy or a discriminator requires a payload version bump and a migration.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SessionEvent))]
internal sealed partial class EventJsonContext : JsonSerializerContext;

/// <summary>
/// An event payload in its stored form, for a surface that shows the log as it is (the windowed app's Audit): the JSON the
/// log keeps and the type name its discriminator gives (<c>tool_call_issued</c>). Payloads read from the log were scrubbed
/// of credentials when they were appended (C-040).
/// </summary>
public static class EventPayload
{
    /// <summary>The payload as the log stores it, with its <c>type</c> discriminator, on one line.</summary>
    public static string Json(SessionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EventJson.Serialize(value);
    }

    /// <summary>The payload's stored type name ("user_message"), the same for every payload of its class.</summary>
    public static string Type(SessionEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Types.GetOrAdd(value.GetType(), _ =>
        {
            using var document = JsonDocument.Parse(Json(value));
            return document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "";
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> Types = new();
}

internal static class EventJson
{
    internal const int Version = 1;

    // Always through the base type, so the "type" discriminator is written.
    internal static string Serialize(SessionEvent value) =>
        JsonSerializer.Serialize(value, EventJsonContext.Default.SessionEvent);

    internal static SessionEvent Deserialize(int version, string json)
    {
        if (version != Version)
            throw new InvalidDataException($"Unsupported session event payload version {version}.");
        try
        {
            return JsonSerializer.Deserialize(json, EventJsonContext.Default.SessionEvent)
                ?? throw new InvalidDataException("Null session event payload.");
        }
        // A payload without a discriminator surfaces as NotSupportedException for the abstract base.
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Invalid or unknown session event payload.", exception);
        }
    }
}
