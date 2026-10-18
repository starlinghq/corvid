using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Evals;

/// <summary>A projection the trajectory must produce at a log position. Null <see cref="Expected"/> means not yet recorded.</summary>
public sealed record GoldenCheck(long Through, bool ExpandCompactions, JsonElement? Expected);

/// <summary>A recorded session history and the projections it must replay to.</summary>
/// <remarks>Events are stored in the event log's persisted format (payload version 1), so a fixture is a real log.</remarks>
public sealed record GoldenTrajectory(string Id, string Description, ImmutableArray<SessionEvent> Events,
    ImmutableArray<GoldenCheck> Checks);

/// <summary>Reads and writes the versioned fixture format. Unknown fields and versions fail rather than being ignored.</summary>
public static class GoldenTrajectoryFile
{
    public const string Format = "corvid-golden-trajectory";
    public const int FormatVersion = 1;

    private static readonly string[] TopLevel = ["format", "format_version", "payload_version", "id", "description", "events", "checks"];
    private static readonly string[] CheckFields = ["through", "expand_compactions", "expected"];

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // Fixtures are reviewed by people; keep quotes, '+' and non-ASCII text readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static GoldenTrajectory Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{path}: {exception.Message}", exception);
        }
    }

    public static GoldenTrajectory Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Golden trajectory is not valid JSON.", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            RequireObject(root, "$", TopLevel);
            if (String(root, "format") != Format)
                throw new InvalidDataException($"Expected format '{Format}'.");
            if (Integer(root, "format_version") != FormatVersion)
                throw new InvalidDataException($"Unsupported golden trajectory format version; this harness reads version {FormatVersion}.");
            // Range-check before narrowing so an oversized version cannot wrap round to a supported one.
            var payloadVersion = Integer(root, "payload_version") is var version and >= 0 and <= int.MaxValue
                ? (int)version
                : throw new InvalidDataException("'payload_version' is out of range.");

            var events = ImmutableArray.CreateBuilder<SessionEvent>();
            foreach (var item in Array(root, "events"))
            {
                try
                {
                    events.Add(EventJson.Deserialize(payloadVersion, item.GetRawText()));
                }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException($"$.events[{events.Count}]: {exception.Message}", exception);
                }
            }

            var checks = ImmutableArray.CreateBuilder<GoldenCheck>();
            foreach (var item in Array(root, "checks"))
            {
                var path = $"$.checks[{checks.Count}]";
                RequireObject(item, path, CheckFields);
                var through = Integer(item, "through");
                if (through < 0 || through > events.Count)
                    throw new InvalidDataException($"{path}.through must be between 0 and the event count ({events.Count}).");
                var expand = item.TryGetProperty("expand_compactions", out var flag) &&
                    (flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? flag.GetBoolean()
                        : throw new InvalidDataException($"{path}.expand_compactions must be a boolean."));
                if (!item.TryGetProperty("expected", out var expected))
                    throw new InvalidDataException($"{path}.expected is required (null until recorded).");
                checks.Add(new(through, expand, expected.ValueKind == JsonValueKind.Null ? null : expected.Clone()));
            }

            var id = String(root, "id");
            if (id.Length == 0)
                throw new InvalidDataException("$.id must not be empty.");
            return new(id, String(root, "description"), events.ToImmutable(), checks.ToImmutable());
        }
    }

    public static string Serialize(GoldenTrajectory trajectory)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("format_version", FormatVersion);
            writer.WriteNumber("payload_version", EventJson.Version);
            writer.WriteString("id", trajectory.Id);
            writer.WriteString("description", trajectory.Description);
            writer.WriteStartArray("events");
            foreach (var item in trajectory.Events)
                WriteEvent(writer, item);
            writer.WriteEndArray();
            writer.WriteStartArray("checks");
            foreach (var check in trajectory.Checks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("through", check.Through);
                if (check.ExpandCompactions)
                    writer.WriteBoolean("expand_compactions", true);
                writer.WritePropertyName("expected");
                if (check.Expected is { } expected)
                    expected.WriteTo(writer);
                else
                    writer.WriteNullValue();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    internal static void WriteEvent(Utf8JsonWriter writer, SessionEvent value)
    {
        // Re-parse so the indented writer lays the event out like the rest of the file.
        using var document = JsonDocument.Parse(EventJson.Serialize(value));
        document.RootElement.WriteTo(writer);
    }

    internal static JsonWriterOptions Options => WriterOptions;

    private static void RequireObject(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{path} must be an object.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"{path}.{property.Name} is not a field of format version {FormatVersion}.");
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"'{name}' must be a string.");

    private static long Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw new InvalidDataException($"'{name}' must be an integer.");

    private static JsonElement.ArrayEnumerator Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw new InvalidDataException($"'{name}' must be an array.");
}
