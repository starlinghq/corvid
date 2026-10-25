using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvid.Secrets;
using Microsoft.Extensions.AI;

namespace Corvid.Host.Audit;

/// <summary>
/// A request as the privacy egress gate sent it (G-9), as text a person reads: one block per message part, each opened by
/// its role in brackets (<c>[user]</c>, <c>[tool_call call-1 shell]</c>, <c>[tool_result call-1]</c>), in the order the
/// provider received them. This is Corvid's rendering of the messages it handed the provider's adapter, not the adapter's
/// wire JSON, so sizes are of this text. The gate's placeholders (<c>&lt;EMAIL_1&gt;</c>) stay as they were sent: the gate
/// neutralizes placeholder-shaped text in what it redacts, so in a redacted request every one is the gate's own.
/// </summary>
public static partial class EgressRendering
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Each message as its block of text, credential-scrubbed (C-040) as a second net: the gate never sends a credential
    /// value, and a store without the log's scrub must not keep one either.
    /// </summary>
    public static ImmutableArray<string> Blocks(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return [.. messages.Select(message => CredentialScrub.Scrub(Block(message), CredentialDetector.Egress).Text)];
    }

    /// <summary>The whole request: the tools offered, then the messages' blocks.</summary>
    public static string Full(IEnumerable<string> blocks, IReadOnlyCollection<string> tools)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(tools);
        var lines = new List<string>();
        if (tools.Count > 0)
            lines.Add("[tools offered] " + string.Join(", ", tools));
        lines.AddRange(blocks);
        return string.Join('\n', lines);
    }

    /// <summary>The request's start and end, with an ellipsis line where the middle is left out.</summary>
    public static string Excerpt(string full, int head = 8, int tail = 8)
    {
        ArgumentNullException.ThrowIfNull(full);
        var lines = full.Split('\n');
        if (lines.Length <= head + tail + 1)
            return full;
        return string.Join('\n', lines.Take(head).Append("…").Concat(lines.Skip(lines.Length - tail)));
    }

    /// <summary>How many distinct placeholders of the gate's form (<c>&lt;LABEL_n&gt;</c>) the text holds.</summary>
    public static int Placeholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Placeholder().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>The UTF-8 size of a rendered request.</summary>
    public static long Size(string text) => Encoding.UTF8.GetByteCount(text);

    private static string Block(ChatMessage message)
    {
        var role = message.Role.Value;
        var parts = new List<string>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    parts.Add($"[{role}] {text.Text}");
                    break;
                case TextReasoningContent reasoning:
                    parts.Add($"[{role} reasoning] {reasoning.Text}");
                    break;
                case FunctionCallContent call:
                    parts.Add($"[tool_call {call.CallId} {call.Name}] {Arguments(call.Arguments)}");
                    break;
                case FunctionResultContent result:
                    parts.Add($"[tool_result {result.CallId}]\n{Value(result.Result)}");
                    break;
                case UsageContent:
                    break;
                case DataContent data:
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"[{role} {data.MediaType}, {data.Data.Length} bytes, not shown]"));
                    break;
                default:
                    parts.Add($"[{role} {content.GetType().Name}, not shown]");
                    break;
            }
        }
        return parts.Count == 0 ? $"[{role}]" : string.Join('\n', parts);
    }

    private static string Value(object? value) => value switch
    {
        null => "null",
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString()!,
        // Written again without the default escaping, so a placeholder reads <EMAIL_1>, never <EMAIL_1>.
        JsonElement json => Relaxed(json),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static string Relaxed(JsonElement json)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
            json.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Arguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "{}";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case bool flag:
                        writer.WriteBooleanValue(flag);
                        break;
                    case JsonElement json:
                        json.WriteTo(writer);
                        break;
                    default:
                        writer.WriteStringValue(Value(value));
                        break;
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [GeneratedRegex(@"<[A-Z][A-Z0-9_]*_[0-9]+>", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
