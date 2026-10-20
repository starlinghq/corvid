using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Corvid.Skills;

/// <summary>A value in SKILL.md frontmatter: a scalar (always text), a list or a map.</summary>
public abstract record YamlNode;

public sealed record YamlScalar(string Value) : YamlNode;

public sealed record YamlList(ImmutableArray<YamlNode> Items) : YamlNode;

/// <summary>Keys keep their order; duplicates are refused when parsing.</summary>
public sealed record YamlMap(ImmutableArray<KeyValuePair<string, YamlNode>> Entries) : YamlNode
{
    public YamlNode? this[string key] => Entries.FirstOrDefault(entry => entry.Key == key).Value;

    public YamlMap? Map(string key) => this[key] as YamlMap;

    public string? Text(string key) => this[key] is YamlScalar scalar ? scalar.Value : null;

    /// <summary>A list of scalars, or a single scalar as a one-item list (Hermes writes both).</summary>
    public ImmutableArray<string> Strings(string key) => this[key] switch
    {
        YamlList list => [.. list.Items.OfType<YamlScalar>().Select(item => item.Value).Where(value => value.Length > 0)],
        YamlScalar { Value.Length: > 0 } scalar => [scalar.Value],
        _ => [],
    };
}

public sealed class FrontmatterException(string message, int line) : FormatException($"line {line}: {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// The YAML subset SKILL.md frontmatter uses (agentskills.io, Hermes, Claude Code): block maps and lists by indentation,
/// <c>key: value</c> with plain, single- and double-quoted scalars, flow lists (<c>[a, b]</c>), block scalars (<c>|</c>,
/// <c>&gt;</c>) and comments. Everything else (anchors, aliases, tags, flow maps, tabs, duplicate keys) is refused rather
/// than guessed at, so a skill whose metadata Corvid cannot read exactly is reported and not loaded. No YAML package is
/// used: a core project would have to admit it to the dependency allowlist for a few dozen lines of metadata.
/// </summary>
public static class Frontmatter
{
    private const int MaxDepth = 8;

    public static YamlMap Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        parser.SkipBlank();
        if (parser.AtEnd)
            return new YamlMap([]);
        if (parser.Indent != 0)
            throw new FrontmatterException("the frontmatter must start at column 0", parser.LineNumber);
        var node = parser.Block(0, 0);
        parser.SkipBlank();
        if (!parser.AtEnd)
            throw new FrontmatterException("unexpected indentation", parser.LineNumber);
        return node as YamlMap ?? throw new FrontmatterException("the frontmatter must be a map of keys", 1);
    }

    private sealed class Parser(string[] lines)
    {
        private int _pos;

        public bool AtEnd => _pos >= lines.Length;

        public int LineNumber => _pos + 1;

        public int Indent => IndentOf(lines[_pos]);

        private string Content => lines[_pos].Trim();

        public void SkipBlank()
        {
            while (!AtEnd && IsBlank(lines[_pos]))
                _pos++;
        }

        private static bool IsBlank(string line)
        {
            var trimmed = line.TrimStart(' ');
            return trimmed.Length == 0 || trimmed[0] == '#';
        }

        private int IndentOf(string line)
        {
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
                indent++;
            if (indent < line.Length && line[indent] == '\t')
                throw new FrontmatterException("tabs are not allowed for indentation", LineNumber);
            return indent;
        }

        public YamlNode Block(int indent, int depth)
        {
            if (depth > MaxDepth)
                throw new FrontmatterException("nested too deeply", LineNumber);
            return IsListItem(Content) ? List(indent, depth) : Map(indent, depth);
        }

        private static bool IsListItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

        private YamlList List(int indent, int depth)
        {
            var items = ImmutableArray.CreateBuilder<YamlNode>();
            while (true)
            {
                SkipBlank();
                if (AtEnd || Indent != indent || !IsListItem(Content))
                    break;
                var rest = Content.Length == 1 ? "" : Content[2..].Trim();
                var line = LineNumber;
                _pos++;
                if (rest.Length == 0)
                {
                    SkipBlank();
                    items.Add(!AtEnd && Indent > indent ? Block(Indent, depth + 1) : new YamlScalar(""));
                    continue;
                }
                if (KeySplit(rest) >= 0 && !rest.StartsWith('"') && !rest.StartsWith('\'') && !rest.StartsWith('['))
                    throw new FrontmatterException("maps inside list items are not supported", line);
                items.Add(Inline(rest, line));
            }
            return new YamlList(items.ToImmutable());
        }

        private YamlMap Map(int indent, int depth)
        {
            var entries = ImmutableArray.CreateBuilder<KeyValuePair<string, YamlNode>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                SkipBlank();
                if (AtEnd || Indent < indent)
                    break;
                if (Indent > indent)
                    throw new FrontmatterException("unexpected indentation", LineNumber);
                if (IsListItem(Content))
                    break;
                var content = Content;
                var line = LineNumber;
                var colon = KeySplit(content);
                if (colon <= 0)
                    throw new FrontmatterException("expected 'key: value'", line);
                var key = Unquote(content[..colon].Trim(), line);
                if (key.Length == 0)
                    throw new FrontmatterException("empty key", line);
                if (!keys.Add(key))
                    throw new FrontmatterException($"duplicate key '{key}'", line);
                var rest = StripComment(content[(colon + 1)..]).Trim();
                _pos++;
                YamlNode value;
                if (rest.Length == 0)
                {
                    SkipBlank();
                    // A list may sit at the key's own indentation ("key:\n- a"), as YAML allows.
                    value = AtEnd ? new YamlScalar("")
                        : Indent > indent ? Block(Indent, depth + 1)
                        : Indent == indent && IsListItem(Content) ? List(indent, depth + 1)
                        : new YamlScalar("");
                }
                else if (rest[0] is '|' or '>')
                {
                    value = BlockScalar(rest, indent, line);
                }
                else
                {
                    value = Inline(rest, line);
                }
                entries.Add(KeyValuePair.Create(key, value));
            }
            return new YamlMap(entries.ToImmutable());
        }

        private YamlScalar BlockScalar(string header, int indent, int line)
        {
            var literal = header[0] == '|';
            var chomp = header.Length > 1 ? header[1..].Trim() : "";
            if (chomp is not ("" or "-" or "+"))
                throw new FrontmatterException("unsupported block scalar header", line);
            var collected = new List<string>();
            var blockIndent = -1;
            while (!AtEnd)
            {
                var raw = lines[_pos];
                if (raw.Trim().Length == 0)
                {
                    collected.Add("");
                    _pos++;
                    continue;
                }
                var current = IndentOf(raw);
                if (current <= indent)
                    break;
                if (blockIndent < 0)
                    blockIndent = current;
                if (current < blockIndent)
                    throw new FrontmatterException("block scalar lines must keep their indentation", LineNumber);
                collected.Add(raw[blockIndent..]);
                _pos++;
            }
            var trailing = 0;
            while (collected.Count > 0 && collected[^1].Length == 0)
            {
                collected.RemoveAt(collected.Count - 1);
                trailing++;
            }
            string text;
            if (literal)
            {
                text = string.Join("\n", collected);
            }
            else
            {
                var folded = new StringBuilder();
                for (var i = 0; i < collected.Count; i++)
                {
                    if (i > 0)
                        folded.Append(collected[i].Length == 0 || collected[i - 1].Length == 0 ? "\n" : " ");
                    folded.Append(collected[i]);
                }
                text = folded.ToString().Replace("\n\n", "\n", StringComparison.Ordinal);
            }
            text += chomp switch
            {
                "-" => "",
                "+" => new string('\n', trailing + (collected.Count > 0 ? 1 : 0)),
                _ => collected.Count > 0 ? "\n" : "",
            };
            return new YamlScalar(text);
        }

        private YamlNode Inline(string text, int line)
        {
            if (text[0] is '&' or '*' or '!' or '{' or '%' or '@' or '`')
                throw new FrontmatterException($"'{text[0]}' (anchors, aliases, tags and flow maps) is not supported", line);
            if (text[0] == '[')
                return FlowList(text, line);
            return new YamlScalar(Scalar(text, line));
        }

        private static YamlList FlowList(string text, int line)
        {
            var close = Closing(text, line);
            if (StripComment(text[(close + 1)..]).Trim().Length > 0)
                throw new FrontmatterException("text after a flow list", line);
            var inner = text[1..close];
            var items = ImmutableArray.CreateBuilder<YamlNode>();
            var start = 0;
            var quote = '\0';
            for (var i = 0; i <= inner.Length; i++)
            {
                var c = i < inner.Length ? inner[i] : ',';
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                        i++;
                    else if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c is '[' or '{' or '&' or '*')
                {
                    throw new FrontmatterException("nested collections, anchors and aliases are not supported in a flow list", line);
                }
                else if (c == ',')
                {
                    var item = inner[start..Math.Min(i, inner.Length)].Trim();
                    start = i + 1;
                    if (item.Length == 0)
                    {
                        if (i < inner.Length || items.Count > 0)
                            continue;
                        break;
                    }
                    items.Add(new YamlScalar(Scalar(item, line)));
                }
            }
            return new YamlList(items.ToImmutable());
        }

        private static int Closing(string text, int line)
        {
            var quote = '\0';
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                        i++;
                    else if (c == quote)
                        quote = '\0';
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == ']')
                {
                    return i;
                }
            }
            throw new FrontmatterException("unterminated flow list", line);
        }

        private static string Scalar(string text, int line)
        {
            if (text[0] is '"' or '\'')
            {
                var value = Quoted(text, line, out var end);
                if (StripComment(text[end..]).Trim().Length > 0)
                    throw new FrontmatterException("text after a quoted value", line);
                return value;
            }
            if (text[0] is '&' or '*' or '!' or '{' or '[' or '|' or '>')
                throw new FrontmatterException($"'{text[0]}' is not supported here", line);
            return StripComment(text).Trim();
        }

        private static string Unquote(string text, int line) =>
            text.Length > 0 && text[0] is '"' or '\'' ? Quoted(text, line, out _) : text;

        private static string Quoted(string text, int line, out int end)
        {
            var quote = text[0];
            var value = new StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                var c = text[i];
                if (quote == '\'' && c == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        value.Append('\'');
                        i++;
                        continue;
                    }
                    end = i + 1;
                    return value.ToString();
                }
                if (quote == '"' && c == '"')
                {
                    end = i + 1;
                    return value.ToString();
                }
                if (quote == '"' && c == '\\' && i + 1 < text.Length)
                {
                    var next = text[++i];
                    switch (next)
                    {
                        case 'n': value.Append('\n'); break;
                        case 't': value.Append('\t'); break;
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'u' when i + 4 < text.Length && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                            value.Append((char)code);
                            i += 4;
                            break;
                        default: throw new FrontmatterException($"unsupported escape '\\{next}'", line);
                    }
                    continue;
                }
                value.Append(c);
            }
            throw new FrontmatterException("unterminated quoted value", line);
        }

        /// <summary>The colon that ends a key: followed by a space or the end, outside quotes.</summary>
        private static int KeySplit(string content)
        {
            var quote = '\0';
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (i == 0 && c is '"' or '\'')
                    quote = c;
                else if (c == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
                    return i;
                else if (c == '#' && i > 0 && content[i - 1] == ' ')
                    return -1;
            }
            return -1;
        }

        /// <summary>Removes a trailing comment: <c>#</c> after whitespace, outside quotes.</summary>
        private static string StripComment(string text)
        {
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                }
                else if (c is '"' or '\'' && (i == 0 || text[i - 1] is ' ' or '[' or ','))
                {
                    quote = c;
                }
                else if (c == '#' && (i == 0 || text[i - 1] == ' '))
                {
                    return text[..i];
                }
            }
            return text;
        }
    }
}
