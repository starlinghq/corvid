using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp;

/// <summary>
/// The deterministic check for a poisoned tool description (C-022): text a server lists for the model to read that carries
/// instructions to the agent instead of describing the tool. The markers are the tool-poisoning class's own: an instruction
/// tag (<c>&lt;IMPORTANT&gt;</c>), concealment from the user, "ignore previous instructions", "before using this tool", and
/// a directive to read a credential path. The pattern is the one of Beacon's <c>mcp-poisoned-tool-description</c> rule
/// (Asymptote-Labs/agent-beacon, MIT). It runs over every text that reaches the prompt (the description, the title and each
/// description in the input schema) after format and control characters are removed and whitespace runs are collapsed, so
/// a zero-width character or a line break cannot split a marker.
/// </summary>
public static partial class McpDescriptionScan
{
    /// <summary>The first poisoning marker in the tool's texts, or null when there is none.</summary>
    public static string? Scan(Tool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        foreach (var text in Texts(tool))
            if (Match(text) is { } match)
                return match;
        return null;
    }

    /// <summary>The first poisoning marker in one text, or null.</summary>
    public static string? Match(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        try
        {
            var match = Poisoned().Match(Normalize(text));
            return match.Success ? match.Value : null;
        }
        catch (RegexMatchTimeoutException)
        {
            // The pattern is linear, so this needs a pathological text; one that long is not let through unseen.
            return "(the scan timed out)";
        }
    }

    private static IEnumerable<string> Texts(Tool tool)
    {
        if (tool.Description is { } description)
            yield return description;
        if (tool.Title is { } title)
            yield return title;
        if (tool.InputSchema.ValueKind == JsonValueKind.Object)
            foreach (var text in SchemaDescriptions(tool.InputSchema))
                yield return text;
    }

    /// <summary>Every <c>description</c> and <c>title</c> string anywhere in the schema: properties, nested objects, items.</summary>
    private static IEnumerable<string> SchemaDescriptions(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "description" or "title" && property.Value.ValueKind == JsonValueKind.String)
                        yield return property.Value.GetString()!;
                    else
                        foreach (var text in SchemaDescriptions(property.Value))
                            yield return text;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var text in SchemaDescriptions(item))
                        yield return text;
                break;
        }
    }

    /// <summary>Format and control characters dropped (as <see cref="McpToolMapping"/> drops them), whitespace runs made one space.</summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue;
            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }

    // Beacon's alternatives, unchanged.
    [GeneratedRegex(
        @"<\s*(important|system|secret|instruction|admin)" +
        @"|do not (tell|mention|inform|reveal|report|disclose|notify|let)" +
        @"|ignore (the |all |any )?(previous|prior|above|earlier|foregoing)" +
        @"|before (using|calling|invoking|running) (this|the) tool" +
        @"|read .{0,60}(\.ssh|\.env|\.aws|id_(rsa|ed25519|ecdsa|dsa)|credentials|/etc/passwd|mcp\.json|secrets?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex Poisoned();
}
