using System.Text.Json;
using Corvid.Kernel.Events;

namespace Corvid.Evals;

/// <summary>The canonical, comparable form of a <see cref="SessionSnapshot"/>.</summary>
/// <remarks>
/// Dictionaries are written as ordinal-sorted arrays so output never depends on hash order. The session ID is left
/// out because the runner chooses it; event IDs and timestamps are not part of a snapshot.
/// </remarks>
public static class SnapshotJson
{
    public static JsonElement ToJson(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, GoldenTrajectoryFile.Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("head", snapshot.Head);
            writer.WriteString("goal", snapshot.Goal);

            writer.WriteStartArray("todos");
            foreach (var item in snapshot.Todos)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("text", item.Text);
                writer.WriteBoolean("completed", item.Completed);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("transcript");
            foreach (var entry in snapshot.Transcript)
            {
                writer.WriteStartObject();
                writer.WriteNumber("from", entry.FromSequence);
                writer.WriteNumber("through", entry.ThroughSequence);
                writer.WritePropertyName("event");
                GoldenTrajectoryFile.WriteEvent(writer, entry.Content);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("pending_approvals");
            foreach (var (_, approval) in snapshot.PendingApprovals.OrderBy(item => item.Key, StringComparer.Ordinal))
                GoldenTrajectoryFile.WriteEvent(writer, approval);
            writer.WriteEndArray();

            writer.WriteStartArray("model_profiles");
            foreach (var (slot, profile) in snapshot.ModelProfiles.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("slot", slot);
                writer.WritePropertyName("profile");
                profile.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("decisions");
            foreach (var (_, state) in snapshot.Decisions.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("decision");
                GoldenTrajectoryFile.WriteEvent(writer, state.Decision);
                writer.WriteString("outcome", state.Outcome);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("checkpoints");
            foreach (var checkpoint in snapshot.Checkpoints)
                GoldenTrajectoryFile.WriteEvent(writer, checkpoint);
            writer.WriteEndArray();

            writer.WriteStartArray("issues");
            foreach (var issue in snapshot.Issues)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", issue.Sequence);
                writer.WriteString("reason", issue.Reason);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}

/// <summary>Structural JSON comparison that reports where two values differ.</summary>
public static class JsonDiff
{
    public const int MaxDifferences = 50;

    public static IReadOnlyList<string> Compare(JsonElement expected, JsonElement actual)
    {
        var differences = new List<string>();
        Compare("$", expected, actual, differences);
        return differences;
    }

    private static void Compare(string path, JsonElement expected, JsonElement actual, List<string> differences)
    {
        if (differences.Count >= MaxDifferences)
            return;
        if (expected.ValueKind != actual.ValueKind && !(IsBoolean(expected) && IsBoolean(actual)))
        {
            differences.Add($"{path}: expected {Show(expected)}, actual {Show(actual)}");
            return;
        }
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var actualNames = actual.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var property in expected.EnumerateObject())
                {
                    var child = $"{path}.{property.Name}";
                    if (actual.TryGetProperty(property.Name, out var value))
                        Compare(child, property.Value, value, differences);
                    else
                        differences.Add($"{child}: expected {Show(property.Value)}, actual is missing");
                    actualNames.Remove(property.Name);
                }
                foreach (var name in actualNames.Order(StringComparer.Ordinal))
                    differences.Add($"{path}.{name}: not expected, actual {Show(actual.GetProperty(name))}");
                break;
            case JsonValueKind.Array:
                var left = expected.EnumerateArray().ToArray();
                var right = actual.EnumerateArray().ToArray();
                if (left.Length != right.Length)
                    differences.Add($"{path}: expected {left.Length} items, actual {right.Length}");
                for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
                    Compare($"{path}[{i}]", left[i], right[i], differences);
                break;
            case JsonValueKind.Number:
                if (!NumbersEqual(expected, actual))
                    differences.Add($"{path}: expected {Show(expected)}, actual {Show(actual)}");
                break;
            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                    differences.Add($"{path}: expected {Show(expected)}, actual {Show(actual)}");
                break;
            case JsonValueKind.True or JsonValueKind.False:
                if (expected.GetBoolean() != actual.GetBoolean())
                    differences.Add($"{path}: expected {Show(expected)}, actual {Show(actual)}");
                break;
        }
    }

    // 1, 1.0 and 1e0 are the same number; the serializer's formatting is not part of the contract.
    private static bool NumbersEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.GetRawText() == actual.GetRawText())
            return true;
        if (expected.TryGetDecimal(out var left) && actual.TryGetDecimal(out var right))
            return left == right;
        return expected.GetDouble().Equals(actual.GetDouble());
    }

    private static bool IsBoolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string Show(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= 120 ? text : text[..117] + "...";
    }
}
