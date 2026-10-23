using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Corvid.Secrets;

/// <summary>A scrubbed text and how many credential values were replaced in it.</summary>
public readonly record struct ScrubbedText(string Text, int Count);

/// <summary>A scrubbed JSON element and how many credential values were replaced in it. Unchanged input is returned as given.</summary>
public readonly record struct ScrubbedJson(JsonElement Element, int Count);

/// <summary>
/// The credential scrub on writes (C-040): every value <see cref="CredentialDetector.Find"/> reports is replaced by a
/// non-reversible marker before the text is stored. Used by the event log (every appended event) and the skill scanner.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="CredentialStrength.Shape"/> match keeps its public prefix and gets its kind and the first eight hex digits of
/// the SHA-256 of the whole matched value: <c>ghp_[REDACTED:github-token:3fa91c0e]</c>. The hash is a correlator (the same
/// token in two events has the same marker), not protection: a provider token has too much entropy for 32 bits to confirm a
/// guess. A labelled, URL-password or prose match is a value a person chose, where an unkeyed hash would confirm a dictionary
/// guess, so it gets its kind only: <c>[REDACTED:labelled-value]</c>.
/// </para>
/// <para>
/// Fail closed: a detector timeout, or a credential found in JSON text that does not decode, replaces the whole text or
/// element with <see cref="Unscannable"/>. Markers are exempt (<see cref="CredentialDetector.IsExempt"/>), so the scrub is
/// idempotent, and they hold no <c>&lt;</c> or <c>&gt;</c>, so the egress gate's placeholder restore never touches one. Vault
/// placeholders (<c>vsec_…</c>) are never credentials and pass unchanged.
/// </para>
/// </remarks>
public static class CredentialScrub
{
    /// <summary>What a text or element becomes when it could not be scanned.</summary>
    public const string Unscannable = "[REDACTED:unscannable]";

    private const int HashHexDigits = 8;

    // A shorter value found in JSON text replaces only a leaf that is that value, so "ab" under a label never rewrites "about".
    internal const int MinRawSubstring = 8;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The marker's kind name: <c>github-token</c>, <c>labelled-value</c>. Stable; it is persisted.</summary>
    public static string KindName(CredentialKind kind) => kind switch
    {
        CredentialKind.PrivateKey => "private-key",
        CredentialKind.PrivateKeyHeader => "private-key-header",
        CredentialKind.Authorization => "authorization",
        CredentialKind.AwsAccessKey => "aws-access-key",
        CredentialKind.GitHubToken => "github-token",
        CredentialKind.SlackToken => "slack-token",
        CredentialKind.SkKey => "sk-key",
        CredentialKind.StripeKey => "stripe-key",
        CredentialKind.GitLabToken => "gitlab-token",
        CredentialKind.GoogleApiKey => "google-api-key",
        CredentialKind.HuggingFaceToken => "huggingface-token",
        CredentialKind.Jwt => "jwt",
        CredentialKind.LabelledValue => "labelled-value",
        CredentialKind.UrlPassword => "url-password",
        CredentialKind.ProseValue => "prose-value",
        CredentialKind.EncodedHex => "encoded-hex",
        CredentialKind.EncodedBase64 => "encoded-base64",
        CredentialKind.UserPassword => "user-password",
        _ => "credential",
    };

    /// <summary>
    /// The marker for <paramref name="match"/> in <paramref name="text"/>: the kept prefix, the kind and, for a shape, the hash
    /// prefix of the whole value.
    /// </summary>
    public static string Marker(string text, CredentialMatch match)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Marker(match.ValueIn(text), match.Kind, match.PrefixLength, match.Strength);
    }

    private static string Marker(string value, CredentialKind kind, int prefixLength, CredentialStrength strength)
    {
        var builder = new StringBuilder(prefixLength + 40).Append(value, 0, Math.Min(prefixLength, value.Length))
            .Append("[REDACTED:").Append(KindName(kind));
        if (strength == CredentialStrength.Shape)
            builder.Append(':').Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..HashHexDigits]);
        return builder.Append(']').ToString();
    }

    /// <summary><paramref name="text"/> with every credential value under <paramref name="rules"/> replaced by its marker.</summary>
    public static ScrubbedText Scrub(string text, CredentialRules rules) => Scrub(text, rules, CredentialDetector.Find);

    internal static ScrubbedText Scrub(string text, CredentialRules rules, Func<string, CredentialRules, ImmutableArray<CredentialMatch>> find)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(find);
        if (text.Length == 0)
            return new(text, 0);
        List<Span> spans;
        try
        {
            spans = Spans(text, rules, find);
        }
        catch (RegexMatchTimeoutException)
        {
            return new(Unscannable, 1);
        }
        if (spans.Count == 0)
            return new(text, 0);
        var scrubbed = Render(text, spans, out var count);
        return new(scrubbed, count);
    }

    // Each rule family on its own, so a token the labelled rule also covers (GH_TOKEN=ghp_...?) is still named by its shape:
    // the detector's own merge would name the span by the longer labelled candidate.
    private static List<Span> Spans(string text, CredentialRules rules, Func<string, CredentialRules, ImmutableArray<CredentialMatch>> find)
    {
        var spans = new List<Span>();
        foreach (var family in Families)
            if ((rules & family) != 0)
                foreach (var match in find(text, family))
                    Add(text, new(match.Start, match.Length, match.Kind, match.PrefixLength, match.Strength), spans);
        return spans;
    }

    // A labelled value that holds a shell substitution (API_TOKEN="$(cat ~/.aws/credentials)-v2") is computed when the command
    // runs: the substitution is the command's evidence, which detection rules (C-023) correlate on in the stored event and
    // corvid scan reads back, and it holds no value. Only the literal text around it is replaced, each run on its own; a run
    // with no letter or digit, or that is exempt (a marker, so the scrub stays idempotent), is kept. Kept: $(...), `...` and
    // ${NAME}; ${NAME:-default} holds a literal and is text. A shape inside a substitution ($(echo ghp_...)) is its own span
    // and is still replaced; a literal under no rule inside one ($(echo hunter2)) is kept with it, the price of the evidence.
    private static void Add(string text, Span span, List<Span> spans)
    {
        if (span.Strength != CredentialStrength.Labelled || !HasSubstitution(text.AsSpan(span.Start, span.Length)))
        {
            spans.Add(span);
            return;
        }
        var literal = span.Start;
        for (var i = span.Start; i < span.End;)
        {
            var close = SubstitutionEnd(text, i, span.End);
            if (close < 0)
            {
                i++;
                continue;
            }
            AddLiteral(text, literal, i, span, spans);
            i = literal = close;
        }
        AddLiteral(text, literal, span.End, span, spans);
    }

    private static bool HasSubstitution(ReadOnlySpan<char> value) =>
        value.Contains("$(", StringComparison.Ordinal) || value.Contains("${", StringComparison.Ordinal) || value.Contains('`');

    // The end of the $(...), ${...} or `...` that starts at i, or -1. One left open runs to the value's end: a bare value is
    // cut at whitespace, so TOKEN=$(cat ~/.netrc) arrives as "$(cat".
    private static int SubstitutionEnd(string text, int i, int end)
    {
        if (text[i] == '`')
        {
            var close = text.IndexOf('`', i + 1, end - i - 1);
            return close < 0 ? end : close + 1;
        }
        if (text[i] != '$' || i + 1 >= end)
            return -1;
        // ${NAME} only: ${DB_PASS:-hunter2} holds a literal default, so it is judged as text.
        if (text[i + 1] == '{')
        {
            var close = text.IndexOf('}', i + 2, end - i - 2);
            return close > i + 2 && IsName(text.AsSpan(i + 2, close - i - 2)) ? close + 1 : -1;
        }
        if (text[i + 1] != '(')
            return -1;
        var depth = 0;
        for (var j = i + 1; j < end; j++)
        {
            if (text[j] == '(')
                depth++;
            else if (text[j] == ')' && --depth == 0)
                return j + 1;
        }
        return end;
    }

    private static void AddLiteral(string text, int start, int end, Span span, List<Span> spans)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;
        var run = text.AsSpan(start, end - start);
        if (!HasLetterOrDigit(run) || CredentialDetector.IsExempt(run.ToString()))
            return;
        spans.Add(span with { Start = start, Length = end - start, PrefixLength = 0 });
    }

    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (!char.IsAsciiLetter(name[0]) && name[0] != '_')
            return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        return true;
    }

    private static bool HasLetterOrDigit(ReadOnlySpan<char> run)
    {
        foreach (var c in run)
            if (char.IsLetterOrDigit(c))
                return true;
        return false;
    }

    private static readonly CredentialRules[] Families =
        [CredentialRules.Shapes, CredentialRules.Labelled, CredentialRules.Encoded, CredentialRules.KeyHeader];

    /// <summary>
    /// <paramref name="element"/> with every credential value replaced by its marker, in string leaves and property names,
    /// keeping its structure. The element's JSON text is scanned too, so a key labels its value (<c>{"password": "..."}</c>),
    /// and a direct string or number value of a secret-labelled key (<see cref="CredentialDetector.IsSecretLabel"/>) is a
    /// labelled value. An object or array under such a key is structure, not a value; its own keys are judged on their names,
    /// and its <c>value</c> key is labelled, as is one beside a <c>name</c> or <c>key</c> that is a secret label
    /// (<c>{"name": "DB_PASSWORD", "value": "..."}</c>).
    /// </summary>
    public static ScrubbedJson ScrubJson(JsonElement element, CredentialRules rules) => ScrubJson(element, rules, CredentialDetector.Find);

    internal static ScrubbedJson ScrubJson(JsonElement element, CredentialRules rules,
        Func<string, CredentialRules, ImmutableArray<CredentialMatch>> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)
            return new(element, 0);
        try
        {
            // Values the JSON text shows under a label, decoded as the leaves hold them.
            var raw = new List<(string Value, Span Match)>();
            var json = element.GetRawText();
            foreach (var match in Spans(json, rules, find))
            {
                var value = json.Substring(match.Start, match.Length);
                // The bare-value rule admits "{" for templates, so {"token":{"a":"b"}} offers the object's text: structure.
                if (value.Length == 0 || value[0] is '{' or '[')
                    continue;
                if (value.Contains('\\', StringComparison.Ordinal))
                {
                    if (!TryJsonUnescape(value, out value))
                        return new(UnscannableElement(), 1);
                    if (CredentialDetector.IsExempt(value))
                        continue;
                }
                // A bare value in JSON text runs on past an escaped line break (TOKEN=x\n), which decodes to whitespace.
                value = value.Trim();
                if (value.Length > 0)
                    raw.Add((value, match));
            }
            var walker = new Walker(rules, find, raw);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
                walker.Write(writer, element, labelled: false);
            if (walker.Count == 0)
                return new(element, 0);
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return new(document.RootElement.Clone(), walker.Count);
        }
        catch (RegexMatchTimeoutException)
        {
            return new(UnscannableElement(), 1);
        }
    }

    private static JsonElement UnscannableElement()
    {
        using var document = JsonDocument.Parse("\"" + Unscannable + "\"");
        return document.RootElement.Clone();
    }

    private static bool TryJsonUnescape(string value, out string decoded)
    {
        try
        {
            using var document = JsonDocument.Parse("\"" + value + "\"");
            decoded = document.RootElement.GetString() ?? value;
            return true;
        }
        catch (JsonException)
        {
            decoded = value;
            return false;
        }
    }

    private readonly record struct Span(int Start, int Length, CredentialKind Kind, int PrefixLength, CredentialStrength Strength)
    {
        public int End => Start + Length;
    }

    // Overlapping spans are replaced as one, so no candidate's extent leaks past another's. The earliest span names it,
    // the strongest first (a shape over a labelled value at the same start), then the longest; a shape's hash covers its own
    // value, so the same token has the same marker wherever it appears.
    private static string Render(string text, List<Span> spans, out int count)
    {
        count = 0;
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : a.Strength != b.Strength ? a.Strength.CompareTo(b.Strength)
            : b.Length.CompareTo(a.Length));
        var builder = new StringBuilder(text.Length);
        var position = 0;
        for (var i = 0; i < spans.Count;)
        {
            var named = spans[i];
            var end = named.End;
            for (i++; i < spans.Count && spans[i].Start < end; i++)
                end = Math.Max(end, spans[i].End);
            count++;
            builder.Append(text, position, named.Start - position)
                .Append(Marker(text.Substring(named.Start, named.Length), named.Kind, named.PrefixLength, named.Strength));
            position = end;
        }
        return builder.Append(text, position, text.Length - position).ToString();
    }

    private sealed class Walker(CredentialRules rules, Func<string, CredentialRules, ImmutableArray<CredentialMatch>> find,
        List<(string Value, Span Match)> raw)
    {
        public int Count { get; private set; }

        public void Write(Utf8JsonWriter writer, JsonElement element, bool labelled)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    // {"password": {"value": ...}}, and the k8s and CI env shape {"name": "DB_PASSWORD", "value": ...}: a
                    // "value" key is labelled by its parent's key or by its sibling name.
                    var valueLabelled = labelled || NamesSecret(element);
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(Leaf(property.Name, labelled: false));
                        Write(writer, property.Value, CredentialDetector.IsSecretLabel(property.Name)
                            || (valueLabelled && property.Name.Equals("value", StringComparison.OrdinalIgnoreCase)));
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        Write(writer, item, labelled: false);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(Leaf(element.GetString()!, labelled));
                    break;
                case JsonValueKind.Number:
                    var number = element.GetRawText();
                    var scrubbed = Leaf(number, labelled);
                    if (ReferenceEquals(scrubbed, number))
                        writer.WriteRawValue(number, skipInputValidation: true);
                    else
                        writer.WriteStringValue(scrubbed);
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static bool NamesSecret(JsonElement element)
        {
            foreach (var property in element.EnumerateObject())
                if ((property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("key", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String && CredentialDetector.IsSecretLabel(property.Value.GetString()!))
                    return true;
            return false;
        }

        // The leaf's own matches; a raw value the leaf is, or holds when it is long enough not to rewrite unrelated text
        // (an Authorization value after a scheme the leaf alone does not label); and the whole leaf when its key is a secret
        // label.
        private string Leaf(string text, bool labelled)
        {
            if (text.Length == 0)
                return text;
            var spans = Spans(text, rules, find);
            foreach (var (value, match) in raw)
            {
                var prefix = Math.Min(match.PrefixLength, value.Length);
                // A short one only on the labelled path: "admin" as a password must not rewrite a user or a note that is "admin".
                if (value.Length < MinRawSubstring)
                {
                    if (!labelled)
                        continue;
                    var trimmed = text.AsSpan().Trim();
                    if (trimmed.SequenceEqual(value))
                        spans.Add(new(text.IndexOf(value, StringComparison.Ordinal), value.Length, match.Kind, prefix, match.Strength));
                    continue;
                }
                for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                    spans.Add(new(at, value.Length, match.Kind, prefix, match.Strength));
            }
            if (spans.Count == 0 && labelled && !CredentialDetector.IsExempt(text))
                spans.Add(new(0, text.Length, CredentialKind.LabelledValue, 0, CredentialStrength.Labelled));
            if (spans.Count == 0)
                return text;
            var scrubbed = Render(text, spans, out var count);
            Count += count;
            return scrubbed;
        }
    }
}
