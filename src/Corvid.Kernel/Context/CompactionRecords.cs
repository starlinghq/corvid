using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;

namespace Corvid.Kernel.Context;

/// <summary>
/// The structured compaction record of plan §8 (goal, constraints, decisions, files, open questions, next step): reading it
/// from a summarizer's reply and merging records. Merging is deterministic and loses nothing: lists are unions in order,
/// and the newest goal and next step win, so a later compaction updates fields instead of rewriting prose.
/// </summary>
public static class CompactionRecords
{
    public static CompactionSummary Empty { get; } = new(null, [], [], [], [], null);

    /// <summary>
    /// Reads a record from a model's reply: the outermost JSON object in it, with the six fields (snake_case). A list given as
    /// one string is one item; other values are kept as their JSON text. Null when there is no object or none of the fields.
    /// </summary>
    public static CompactionSummary? Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var start = reply.IndexOf('{', StringComparison.Ordinal);
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            using var document = JsonDocument.Parse(reply.AsMemory(start, end - start + 1),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string[] fields = ["goal", "constraints", "decisions", "files", "open_questions", "next_step"];
            if (!fields.Any(field => root.TryGetProperty(field, out _)))
                return null;
            return new CompactionSummary(Text(root, "goal"), List(root, "constraints"), List(root, "decisions"), List(root, "files"),
                List(root, "open_questions"), Text(root, "next_step"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Merges records oldest first: ordered, de-duplicated unions of the lists, and the newest goal and next step.</summary>
    public static CompactionSummary Merge(IEnumerable<CompactionSummary> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        string? goal = null, next = null;
        var constraints = new Union();
        var decisions = new Union();
        var files = new Union();
        var questions = new Union();
        foreach (var record in records)
        {
            goal = Clean(record.Goal) ?? goal;
            next = Clean(record.NextStep) ?? next;
            constraints.Add(record.Constraints);
            decisions.Add(record.Decisions);
            files.Add(record.Files);
            questions.Add(record.OpenQuestions);
        }
        return new CompactionSummary(goal, constraints.ToArray(), decisions.ToArray(), files.ToArray(), questions.ToArray(), next);
    }

    /// <summary>The record as the summarizer is asked to write it, for prompts and tests.</summary>
    public static string ToJson(CompactionSummary record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("goal", record.Goal);
            Write(writer, "constraints", record.Constraints);
            Write(writer, "decisions", record.Decisions);
            Write(writer, "files", record.Files);
            Write(writer, "open_questions", record.OpenQuestions);
            writer.WriteString("next_step", record.NextStep);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, string name, ImmutableArray<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values.IsDefault ? [] : values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? Clean(value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => string.Join("; ", value.EnumerateArray().Select(Item)),
            _ => value.GetRawText(),
        }) : null;

    private static ImmutableArray<string> List(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return [];
        var items = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(Item),
            JsonValueKind.Null => [],
            _ => [Item(value)],
        };
        var union = new Union();
        union.Add([.. items]);
        return union.ToArray();
    }

    private static string Item(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Union
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<string> _items = [];

        public void Add(ImmutableArray<string> values)
        {
            foreach (var value in values.IsDefault ? [] : values)
                if (Clean(value) is { } item && _seen.Add(item))
                    _items.Add(item);
        }

        public ImmutableArray<string> ToArray() => [.. _items];
    }
}
