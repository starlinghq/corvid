using System.Text.Json;

namespace Corvid.Kernel.Turns;

/// <summary>
/// Checks a tool call's arguments against the tool's JSON Schema (plan §7 step 3) before anything runs, so the repair
/// message can name every problem at once. It covers the subset Corvid.Tools.Generators emits: an object of named
/// properties, <c>required</c>, <c>type</c> (one or a list, with <c>null</c>), <c>enum</c> and array <c>items</c>. Formats
/// (dates, UUIDs, URIs) and per-type ranges are checked by the tool's own binding when it is invoked (C-005).
/// </summary>
internal static class ArgumentValidator
{
    private const int MaxErrors = 8;

    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement arguments)
    {
        var errors = new List<string>();
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"arguments must be a JSON object; got {Kind(arguments)}");
            return errors;
        }
        if (schema.ValueKind != JsonValueKind.Object)
            return errors;

        var properties = schema.TryGetProperty("properties", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)
            : [];
        // Small models invent parameter names; the binder would ignore them, so the intended value would be lost silently.
        foreach (var property in arguments.EnumerateObject())
        {
            if (properties.TryGetValue(property.Name, out var propertySchema))
                Check(propertySchema, property.Value, property.Name, errors);
            else
                errors.Add($"unknown argument '{property.Name}'" + (properties.Count == 0 ? "; the tool takes no arguments"
                    : $"; expected one of: {string.Join(", ", properties.Keys)}"));
        }
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var name in required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!))
                if (!arguments.TryGetProperty(name, out _))
                    errors.Add($"missing required argument '{name}'");
        return errors.Count > MaxErrors ? [.. errors.Take(MaxErrors), $"and {errors.Count - MaxErrors} more"] : errors;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return;
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind switch
            {
                JsonValueKind.String => [type.GetString()!],
                JsonValueKind.Array => type.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray(),
                _ => Array.Empty<string>(),
            };
            if (allowed.Length > 0 && !allowed.Any(name => Matches(name, value)))
            {
                errors.Add($"'{path}' must be {string.Join(" or ", allowed)}; got {Kind(value)} {Show(value)}");
                return;
            }
        }
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array
            && !values.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value)))
            errors.Add($"'{path}' is {Show(value)}, which is not one of: {string.Join(", ", values.EnumerateArray().Select(Show))}");
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                Check(items, item, $"{path}[{index++}]", errors);
        }
    }

    private static bool Matches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        // As the binder reads it: 3.0 is not an integer to ToolArguments.ReadInt64.
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };

    private static string Show(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
