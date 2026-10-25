using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Corvid.Host.Configuration;

/// <summary>
/// Edits JSON with comments (<c>corvid.json</c>) in place: a key's value is replaced, a key is added to the object that
/// should hold it, or removed, and every other byte of the text (comments, layout, key order, the case of its keys) stays
/// as it was. Keys match without regard to case, as the configuration reads them; the first match is the one edited.
/// </summary>
internal static class JsoncText
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true };
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The text with the value at <paramref name="path"/> set to <paramref name="value"/>, or removed when it is null
    /// (nothing changes when there is nothing to remove). Objects missing on the way are made; an array is entered by index.
    /// </summary>
    /// <exception cref="JsonException">The text is not JSON.</exception>
    /// <exception cref="InvalidOperationException">The path names an array element by something other than its index.</exception>
    public static string Set(string text, IReadOnlyList<string> path, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
            throw new ArgumentException("A key has at least one part.", nameof(path));
        var bytes = Utf8.GetBytes(text);
        var root = Parse(bytes);
        if (root.Kind != JsonTokenType.StartObject)
            throw new JsonException("The configuration must be one JSON object.");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\r', StringComparison.Ordinal) && !text.Contains('\n', StringComparison.Ordinal) ? "\r" : "\n";
        var unit = IndentUnit(bytes, root);

        var node = root;
        Node? parent = null;
        var index = -1;
        var depth = 0;
        while (depth < path.Count && Find(node, path[depth]) is { } found)
        {
            (parent, index) = (node, found);
            node = node.Items[found].Value;
            depth++;
        }

        if (depth == path.Count)
        {
            if (value is null)
                return Utf8.GetString(RemoveItem(bytes, parent!, index));
            var item = parent!.Items[index];
            var indent = LineIndent(bytes, item.Start);
            return Utf8.GetString(Splice(bytes, node.Start, node.End, Serialize(value, indent, unit, newline)));
        }

        if (value is null)
            return text; // nothing to remove below a key that is absent
        var rest = path.Skip(depth).ToList();
        switch (node.Kind)
        {
            case JsonTokenType.StartObject:
                return Utf8.GetString(Insert(bytes, node, JsonSerializer.Serialize(rest[0]), Nest(rest.Skip(1), value), unit, newline));
            case JsonTokenType.StartArray when int.TryParse(rest[0], out var at) && at == node.Items.Count:
                return Utf8.GetString(Insert(bytes, node, null, Nest(rest.Skip(1), value), unit, newline));
            case JsonTokenType.StartArray:
                throw new InvalidOperationException($"'{rest[0]}' cannot be set inside an array except by its index.");
            default:
                // A plain value stands where an object is needed: the object takes its place.
                var indent = parent is null ? "" : LineIndent(bytes, parent.Items[index].Start);
                return Utf8.GetString(Splice(bytes, node.Start, node.End, Serialize(Nest(rest, value), indent, unit, newline)));
        }
    }

    // ---- Reading ----

    private sealed record Item(string? Name, int Start, Node Value);

    private sealed class Node(JsonTokenType kind, int start)
    {
        public JsonTokenType Kind { get; } = kind;

        public int Start { get; } = start;

        public int End { get; set; }

        public List<Item> Items { get; } = [];
    }

    private static Node Parse(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        if (!Next(ref reader))
            throw new JsonException("The text holds no JSON value.");
        var root = Read(ref reader);
        if (Next(ref reader))
            throw new JsonException("The text holds more than one JSON value.");
        return root;
    }

    private static bool Next(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.Comment)
                return true;
        }
        return false;
    }

    private static Node Read(ref Utf8JsonReader reader)
    {
        var node = new Node(reader.TokenType, checked((int)reader.TokenStartIndex));
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                while (Next(ref reader) && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString()!;
                    var start = checked((int)reader.TokenStartIndex);
                    Next(ref reader);
                    node.Items.Add(new Item(name, start, Read(ref reader)));
                }
                break;
            case JsonTokenType.StartArray:
                while (Next(ref reader) && reader.TokenType != JsonTokenType.EndArray)
                {
                    var start = checked((int)reader.TokenStartIndex);
                    node.Items.Add(new Item(null, start, Read(ref reader)));
                }
                break;
        }
        // After a value's last token (a closing bracket, or the value itself) the reader has consumed exactly that token.
        node.End = checked((int)reader.BytesConsumed);
        return node;
    }

    private static int? Find(Node node, string key)
    {
        if (node.Kind == JsonTokenType.StartObject)
        {
            var index = node.Items.FindIndex(item => string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : index;
        }
        if (node.Kind == JsonTokenType.StartArray && int.TryParse(key, out var at) && at >= 0 && at < node.Items.Count)
            return at;
        return null;
    }

    // ---- Layout ----

    /// <summary>The whitespace a line starts with, for the line holding <paramref name="position"/>.</summary>
    private static string LineIndent(byte[] bytes, int position)
    {
        var start = LineStart(bytes, position);
        var end = start;
        while (end < bytes.Length && bytes[end] is (byte)' ' or (byte)'\t')
            end++;
        return Encoding.ASCII.GetString(bytes, start, end - start);
    }

    private static int LineStart(byte[] bytes, int position)
    {
        var start = position;
        while (start > 0 && bytes[start - 1] is not ((byte)'\n' or (byte)'\r'))
            start--;
        return start;
    }

    /// <summary>Whether only spaces and tabs come before <paramref name="position"/> on its line.</summary>
    private static bool StartsLine(byte[] bytes, int position)
    {
        for (var i = position - 1; i >= 0 && bytes[i] is not ((byte)'\n' or (byte)'\r'); i--)
        {
            if (bytes[i] is not ((byte)' ' or (byte)'\t'))
                return false;
        }
        return true;
    }

    /// <summary>One level of indentation as the file has it: the root's first key's, or two spaces.</summary>
    private static string IndentUnit(byte[] bytes, Node root)
    {
        if (root.Items.Count > 0 && StartsLine(bytes, root.Items[0].Start) && LineIndent(bytes, root.Items[0].Start) is { Length: > 0 } indent
            && indent.All(c => c == indent[0]))
            return indent;
        return "  ";
    }

    /// <summary>A value as JSON, indented as the file is, its continuation lines starting at <paramref name="indent"/>.</summary>
    private static string Serialize(JsonNode value, string indent, string unit, string newline)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, IndentCharacter = unit[0], IndentSize = unit.Length, NewLine = newline };
        var json = value.ToJsonString(options);
        return json.Replace(newline, newline + indent, StringComparison.Ordinal);
    }

    private static JsonNode Nest(IEnumerable<string> keys, JsonNode value)
    {
        var node = value;
        foreach (var key in keys.Reverse())
            node = new JsonObject { [key] = node };
        return node;
    }

    // ---- Changing ----

    private static byte[] Splice(byte[] bytes, int start, int end, string replacement)
    {
        var inserted = Utf8.GetBytes(replacement);
        var result = new byte[bytes.Length - (end - start) + inserted.Length];
        bytes.AsSpan(0, start).CopyTo(result);
        inserted.CopyTo(result.AsSpan(start));
        bytes.AsSpan(end).CopyTo(result.AsSpan(start + inserted.Length));
        return result;
    }

    private static int SkipBlanks(byte[] bytes, int position)
    {
        while (position < bytes.Length && bytes[position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            position++;
        return position;
    }

    /// <summary>The position after the comma that follows <paramref name="position"/> past whitespace; -1 when none does.</summary>
    private static int AfterComma(byte[] bytes, int position)
    {
        var next = SkipBlanks(bytes, position);
        return next < bytes.Length && bytes[next] == (byte)',' ? next + 1 : -1;
    }

    /// <summary>Where the line holding <paramref name="position"/> ends, when only blanks or a line comment follow it there.</summary>
    private static int? QuietLineEnd(byte[] bytes, int position)
    {
        var i = position;
        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t')
            i++;
        if (i + 1 < bytes.Length && bytes[i] == (byte)'/' && bytes[i + 1] == (byte)'/')
        {
            while (i < bytes.Length && bytes[i] is not ((byte)'\n' or (byte)'\r'))
                i++;
        }
        return i == bytes.Length || bytes[i] is (byte)'\n' or (byte)'\r' ? i : null;
    }

    /// <summary>Adds a member (with <paramref name="name"/>, already quoted) or an element at the end of a container.</summary>
    private static byte[] Insert(byte[] bytes, Node container, string? name, JsonNode value, string unit, string newline)
    {
        var prefix = name is null ? "" : name + ": ";
        if (container.Items.Count == 0)
        {
            // Empty: the new item on a line of its own, one level in from the line that opens the container.
            var outer = LineIndent(bytes, container.Start);
            var inner = outer + unit;
            var item = newline + inner + prefix + Serialize(value, inner, unit, newline);
            var blank = Utf8.GetString(bytes, container.Start + 1, container.End - container.Start - 2).Trim().Length == 0;
            return blank
                ? Splice(bytes, container.Start + 1, container.End - 1, item + newline + outer)
                : Splice(bytes, container.Start + 1, container.Start + 1, item);
        }

        var last = container.Items[^1];
        var comma = AfterComma(bytes, last.Value.End);
        if (!StartsLine(bytes, last.Start))
        {
            // Written on one line ({ "a": 1 }): the new item joins it the same way.
            var compact = prefix + value.ToJsonString();
            return comma < 0
                ? Splice(bytes, last.Value.End, last.Value.End, ", " + compact)
                : Splice(bytes, comma, comma, " " + compact + ",");
        }

        var indent = LineIndent(bytes, last.Start);
        var text = newline + indent + prefix + Serialize(value, indent, unit, newline);
        var after = comma < 0 ? last.Value.End : comma;
        // After a comment ending the last item's line, so the comment stays with the item it describes.
        var at = QuietLineEnd(bytes, after) ?? after;
        var result = Splice(bytes, at, at, text);
        return comma < 0 ? Splice(result, last.Value.End, last.Value.End, ",") : result;
    }

    /// <summary>Removes a container's item with the comma that goes with it, and its line when nothing else is on it.</summary>
    private static byte[] RemoveItem(byte[] bytes, Node container, int index)
    {
        var item = container.Items[index];
        int start, end;
        if (index < container.Items.Count - 1)
        {
            var comma = AfterComma(bytes, item.Value.End);
            if (comma < 0)
            {
                (start, end) = (item.Start, container.Items[index + 1].Start);
            }
            else
            {
                (start, end) = (item.Start, comma);
                while (end < bytes.Length && bytes[end] is (byte)' ' or (byte)'\t')
                    end++;
                // On a line of its own, the line goes, with a comment ending it (which is about the item).
                if (StartsLine(bytes, item.Start) && QuietLineEnd(bytes, comma) is { } lineEnd)
                {
                    start = LineStart(bytes, item.Start);
                    end = lineEnd < bytes.Length && bytes[lineEnd] == (byte)'\r' && lineEnd + 1 < bytes.Length && bytes[lineEnd + 1] == (byte)'\n'
                        ? lineEnd + 2
                        : Math.Min(lineEnd + 1, bytes.Length);
                }
            }
        }
        else if (index > 0 && !StartsLine(bytes, item.Start))
        {
            // The last item, on the line of the one before ({ "a": 1, "b": 2 }): from the comma before it.
            var previous = container.Items[index - 1].Value.End;
            var comma = AfterComma(bytes, previous);
            (start, end) = (comma < 0 ? previous : comma - 1, item.Value.End);
        }
        else
        {
            // The only item, or the last on a line of its own: its line goes, and then the comma before it, so the item
            // before ends the container; comments on lines of their own stay.
            (start, end) = (item.Start, item.Value.End);
            if (AfterComma(bytes, end) is var trailing and >= 0)
                end = trailing;
            if (StartsLine(bytes, item.Start) && QuietLineEnd(bytes, end) is { } lineEnd)
            {
                start = LineStart(bytes, item.Start);
                end = lineEnd;
                // The line's own break goes too, so what follows closes on the next line.
                if (start > 0 && bytes[start - 1] == (byte)'\n')
                    start -= start > 1 && bytes[start - 2] == (byte)'\r' ? 2 : 1;
                else if (start > 0 && bytes[start - 1] == (byte)'\r')
                    start--;
            }
            var result = Splice(bytes, start, end, "");
            if (index > 0 && AfterComma(result, container.Items[index - 1].Value.End) is var comma and >= 0 && comma <= start)
                result = Splice(result, comma - 1, comma, "");
            return result;
        }
        return Splice(bytes, start, end, "");
    }
}
