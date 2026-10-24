using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Corvid.App.Core.Settings;

/// <summary>
/// Reading and writing a <c>corvid.json</c> the way the Settings page needs it, for the demo's in-memory copy and the
/// engine's real file alike: parsing with its line and position, a key written twice, hidden characters in the text or in
/// an escape, and each typed setting's value as text.
/// </summary>
public static class SettingsJson
{
    /// <summary>Comments and trailing commas, as the engine's configuration reads the file.</summary>
    public static JsonDocumentOptions Reading { get; } = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A text box hands back its line breaks as carriage returns; they are line breaks, not hidden characters.</summary>
    public static string Lines(string? json) => (json ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>A typed setting's value in the file (under <c>Corvid</c>), as text; null when the file does not set it.</summary>
    public static string? Read(JsonObject? corvid, SettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        JsonNode? node = corvid;
        foreach (var part in definition.Key.Split(':'))
            node = node is JsonObject obj && Child(obj, part) is { } child ? child : null;
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(),
        };
    }

    /// <summary>The <c>Corvid</c> object of a file, found ignoring case as the configuration reads keys.</summary>
    public static JsonObject? Corvid(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return Child(root, "Corvid") as JsonObject;
    }

    /// <summary>
    /// The JSON a typed value is written as: a boolean for a toggle, a number for a number, otherwise a string; null (the
    /// key removed, so its default applies) for an empty path, address or text.
    /// </summary>
    public static JsonNode? Node(SettingDefinition definition, string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        value = (value ?? "").Trim();
        if (value.Length == 0 && definition.Kind is SettingKind.Path or SettingKind.Url or SettingKind.Text)
            return null;
        return definition.Kind switch
        {
            SettingKind.Toggle => JsonValue.Create(bool.Parse(value)),
            SettingKind.Number when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) => JsonValue.Create(whole),
            SettingKind.Number => JsonValue.Create(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(value),
        };
    }

    /// <summary>Sets a typed setting in a file's JSON (making <c>Corvid</c> and the objects on the way), or removes it for an empty value.</summary>
    public static void Write(JsonObject root, SettingDefinition definition, string value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(definition);
        if (root["Corvid"] is not JsonObject corvid)
            root["Corvid"] = corvid = new JsonObject();
        var parts = definition.Key.Split(':');
        var parent = corvid;
        foreach (var part in parts[..^1])
        {
            if (parent[part] is not JsonObject next)
                parent[part] = next = new JsonObject();
            parent = next;
        }

        var name = parts[^1];
        if (Node(definition, value) is { } node)
            parent[name] = node;
        else
            parent.Remove(name);
    }

    /// <summary>Parses the text as one JSON object; a syntax error, a repeated key or another shape is the problem to show.</summary>
    public static bool TryRead(string json, out JsonObject? root, out SettingProblem? problem)
    {
        root = null;
        problem = null;
        try
        {
            // JsonNode takes a key twice and then throws on the first read of that object; the engine's configuration
            // would keep the last silently. Neither is what the person meant, so a repeated key is a problem to fix.
            if (DuplicateKey(json ?? "") is { } duplicate)
            {
                problem = duplicate;
                return false;
            }

            if (JsonNode.Parse(json ?? "", documentOptions: Reading) is JsonObject obj)
            {
                root = obj;
                return true;
            }

            problem = new SettingProblem(null, "corvid.json must be one JSON object, { … }, with the settings under \"Corvid\".");
            return false;
        }
        catch (JsonException exception)
        {
            var line = (exception.LineNumber ?? 0) + 1;
            var position = (exception.BytePositionInLine ?? 0) + 1;
            var reason = exception.Message.Split(" Path:", 2)[0].Split(" LineNumber:", 2)[0].Trim();
            problem = new SettingProblem(null,
                $"corvid.json does not parse at line {line.ToString(CultureInfo.InvariantCulture)}, position {position.ToString(CultureInfo.InvariantCulture)}: {SafeText.Clean(reason)}");
            return false;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Typed on every keystroke: whatever the parser throws is a message under the editor, never a crash.
            root = null;
            problem = new SettingProblem(null, $"corvid.json could not be read: {SafeText.Clean(exception.Message)}");
            return false;
        }
    }

    /// <summary>
    /// Hidden characters in a parsed text: the first line holding one, or else the first name or string value that holds
    /// one written as a JSON escape (which passes the scan of the text and arrives in the value). Empty when there are none.
    /// </summary>
    public static IReadOnlyList<SettingProblem> Hidden(string json, JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        json = Lines(json);
        var line = HiddenLine(json);
        if (line > 0)
        {
            return [new SettingProblem(null, $"Line {line.ToString(CultureInfo.InvariantCulture)} holds hidden characters: " +
                $"{SafeText.Reveal(json.Split('\n')[line - 1].Trim())}. Remove them before saving.")];
        }

        if (Strings(root, "").FirstOrDefault() is { Path: not null } escaped)
        {
            return [new SettingProblem(null, $"{SafeText.Reveal(escaped.Path)} holds hidden characters written as an escape: " +
                $"{SafeText.Reveal(escaped.Text)}. Remove them before saving.")];
        }

        return [];
    }

    /// <summary>
    /// The first key an object holds twice ("duplicate key \"Workspace\" at line 4"), or null. Keys are compared ignoring
    /// case, as the engine's configuration reads them. A text that does not parse is left to the parser's message.
    /// </summary>
    private static SettingProblem? DuplicateKey(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var objects = new Stack<HashSet<string>?>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        objects.Push(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                        break;
                    case JsonTokenType.StartArray:
                        objects.Push(null);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        objects.Pop();
                        break;
                    case JsonTokenType.PropertyName when objects.Count > 0 && objects.Peek() is { } keys:
                        var name = reader.GetString() ?? "";
                        if (!keys.Add(name))
                        {
                            var line = 1 + bytes.AsSpan(0, (int)reader.TokenStartIndex).Count((byte)'\n');
                            return new SettingProblem(null, $"corvid.json has a duplicate key {Quote(name)} at line {line.ToString(CultureInfo.InvariantCulture)}: " +
                                "an object names each key once (ignoring case). Remove or rename one.");
                        }

                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not well-formed: JsonNode's message says where.
        }

        return null;
    }

    private static string Quote(string name) => $"\"{SafeText.Reveal(name)}\"";

    private static JsonNode? Child(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var exact) ? exact
        : obj.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Every property name and string value holding hidden characters, with its path ("Corvid:Mcp:Servers:notes:Command").</summary>
    private static IEnumerable<(string Path, string Text)> Strings(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    var at = path.Length == 0 ? name : $"{path}:{name}";
                    if (SafeText.HasHidden(name))
                        yield return (at, name);
                    foreach (var found in Strings(child, at))
                        yield return found;
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var found in Strings(array[i], $"{path}:{i.ToString(CultureInfo.InvariantCulture)}"))
                        yield return found;
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && SafeText.HasHidden(text):
                yield return (path, text);
                break;
        }
    }

    private static int HiddenLine(string json)
    {
        var lines = (json ?? "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (SafeText.HasHidden(lines[i]))
                return i + 1;
        }

        return 0;
    }
}
