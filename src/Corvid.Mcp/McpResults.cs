using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp;

/// <summary>A tool result as the generator reads it: text, capped, with non-text content described rather than inlined.</summary>
internal sealed record ShapedResult(string Text, JsonNode? Structured, bool Truncated);

internal static class McpResults
{
    /// <summary>
    /// Text blocks joined by newlines; images, audio and binary resources described by type and size (as web fetch does for
    /// non-text bodies); structured content kept only when there is no text, since servers usually repeat it as text. The
    /// whole is held to <paramref name="maxChars"/>, keeping the start.
    /// </summary>
    public static ShapedResult Shape(CallToolResult result, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        foreach (var block in result.Content ?? [])
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(block switch
            {
                TextContentBlock text => text.Text,
                ImageContentBlock image => $"[image: {image.MimeType}, {image.DecodedData.Length.ToString(CultureInfo.InvariantCulture)} bytes, not shown]",
                AudioContentBlock audio => $"[audio: {audio.MimeType}, {audio.DecodedData.Length.ToString(CultureInfo.InvariantCulture)} bytes, not shown]",
                ResourceLinkBlock link => $"[resource link: {link.Name} {link.Uri}]",
                EmbeddedResourceBlock { Resource: TextResourceContents text } => text.Text,
                EmbeddedResourceBlock embedded => $"[resource: {embedded.Resource.Uri} {embedded.Resource.MimeType}, not shown]",
                _ => $"[{block.Type} content, not shown]",
            });
        }

        JsonNode? structured = null;
        if (builder.Length == 0 && result.StructuredContent is { } content)
        {
            var raw = content.GetRawText();
            if (raw.Length <= maxChars)
                structured = JsonNode.Parse(raw);
            else
                builder.Append(raw);
        }

        var truncated = builder.Length > maxChars;
        var output = truncated ? Cut(builder.ToString(), maxChars) : builder.ToString();
        return new ShapedResult(output, structured, truncated);
    }

    /// <summary>An argument value as the JSON the server is sent.</summary>
    public static JsonElement ToElement(object? value) => value switch
    {
        JsonElement element => element.Clone(),
        null => JsonSerializer.SerializeToElement((string?)null, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(string))),
        _ => JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions.GetTypeInfo(value.GetType())),
    };

    private static string Cut(string text, int maxChars)
    {
        var end = maxChars;
        // Never split a surrogate pair.
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end] + $"\n[cut: {(text.Length - end).ToString(CultureInfo.InvariantCulture)} more characters]";
    }
}
