using System.Text.Json;

namespace Corvid.Kernel.Turns;

/// <summary>
/// A5: a child session's answer checked against the output schema its parent gave (plan §14, "validated on return"). The
/// answer is model text: the JSON is the whole text, or a fenced <c>```json</c> block in it, or the outermost object in it.
/// The schema subset is the tool arguments' (<see cref="ArgumentValidator"/>): an object of named properties,
/// <c>required</c>, <c>type</c>, <c>enum</c> and array <c>items</c>; an unknown property is an error, as for a tool call.
/// </summary>
public static class OutputSchema
{
    /// <summary>The longest answer read for JSON.</summary>
    public const int MaxChars = 64_000;

    /// <summary>Whether a schema is one this can check: a JSON object whose <c>type</c>, if any, is <c>object</c>.</summary>
    public static bool IsSupported(JsonElement schema, out string? why)
    {
        why = schema.ValueKind != JsonValueKind.Object ? "an output schema is a JSON object"
            : schema.TryGetProperty("type", out var type) && !(type.ValueKind == JsonValueKind.String && type.GetString() == "object")
                ? "an output schema describes an object (\"type\": \"object\")"
                : null;
        return why is null;
    }

    /// <summary>Reads the JSON object in <paramref name="text"/> and checks it; the errors are empty when it meets the schema.</summary>
    /// <param name="value">The object read, when there was one (also when it does not meet the schema).</param>
    public static IReadOnlyList<string> Validate(JsonElement schema, string? text, out JsonElement? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return ["the answer is empty, not a JSON object"];
        if (text.Length > MaxChars)
            return [$"the answer is {text.Length} characters; at most {MaxChars} are read for JSON"];
        if (Read(text) is not { } parsed)
            return ["the answer holds no JSON object"];
        value = parsed;
        return ArgumentValidator.Validate(schema, parsed);
    }

    private static JsonElement? Read(string text)
    {
        var trimmed = text.Trim();
        foreach (var candidate in Candidates(trimmed))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string text)
    {
        yield return text;
        var fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = text.IndexOf('\n', fence);
            var end = start < 0 ? -1 : text.IndexOf("```", start, StringComparison.Ordinal);
            if (end > start)
                yield return text[(start + 1)..end];
        }
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open >= 0 && close > open)
            yield return text[open..(close + 1)];
    }
}
