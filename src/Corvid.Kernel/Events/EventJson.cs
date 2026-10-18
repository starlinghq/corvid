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
