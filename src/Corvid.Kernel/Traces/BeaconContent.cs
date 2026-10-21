using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Corvid.Kernel.Traces;

/// <summary>One retained string after the export pipeline: redacted, then cut to the per-string limit.</summary>
/// <param name="Stored">What the line carries.</param>
/// <param name="Hash">SHA-256 hex of the redacted text before truncation.</param>
/// <param name="Bytes">UTF-8 length of the original text.</param>
internal sealed record RetainedText(string Stored, bool Redacted, bool Truncated, string Hash, int Bytes);

/// <summary>
/// Content handling for the Beacon export (C-027): Beacon's per-string limit, its <c>content</c> marker and its
/// metadata-only strip (<c>StripToMetadata</c>), and the per-event size cap.
/// </summary>
internal static class BeaconContent
{
    /// <summary>Beacon's <c>DefaultStringLimit</c>: each retained string is at most this many UTF-8 bytes.</summary>
    public const int StringLimit = 4096;

    private const string TruncationMarker = "...[truncated]";

    /// <summary>Redacts, hashes, then truncates. Redaction comes first so a cut never leaves part of a secret behind.</summary>
    /// <remarks>
    /// The hash is over the redacted text, where Beacon's <c>RetainedContent</c> hashes the original: a hash of the original
    /// would let anyone holding the export confirm a guess at a redacted secret offline. It is still stable across
    /// truncation and equal for equal text, which is what it is for.
    /// </remarks>
    public static RetainedText Retain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = SecretRedactor.Redact(text);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(redacted)));
        var stored = Truncate(redacted, StringLimit);
        return new RetainedText(stored, !string.Equals(redacted, text, StringComparison.Ordinal), stored.Length != redacted.Length, hash,
            Encoding.UTF8.GetByteCount(text));
    }

    /// <summary>Beacon's <c>TruncateString</c>: at most <paramref name="limit"/> bytes, cut on a character boundary, marked.</summary>
    public static string Truncate(string value, int limit)
    {
        if (Encoding.UTF8.GetByteCount(value) <= limit)
            return value;
        return limit < 32 ? TruncateUtf8(value, limit) : TruncateUtf8(value, limit - 15) + TruncationMarker;
    }

    private static string TruncateUtf8(string value, int limit)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= limit)
            return value;
        // Back up to the first byte of a character, so a multi-byte character is never split.
        while (limit > 0 && (bytes[limit] & 0xC0) == 0x80)
            limit--;
        return Encoding.UTF8.GetString(bytes, 0, limit);
    }

    /// <summary>The marker for an event that retains text: Beacon's <c>ContentInfo</c>, in its field order.</summary>
    public static JsonObject Marker(RetainedText primary, bool redacted, bool truncated)
    {
        var marker = new JsonObject { ["retention"] = "full", ["included"] = true };
        if (redacted)
            marker["redacted"] = true;
        if (truncated)
            marker["truncated"] = true;
        marker["hash"] = primary.Hash;
        if (primary.Bytes > 0)
            marker["bytes"] = primary.Bytes;
        return marker;
    }

    /// <summary>
    /// Removes the content Beacon's metadata-only forwarding removes from a full event: prompt text, command output, approval
    /// reason, gen_ai input and output, tool call arguments and result. Ids, names, paths, the command line and decisions stay;
    /// the marker keeps its hash and size and says the content was withheld. Objects left empty are dropped.
    /// </summary>
    /// <param name="message">Also remove the top-level message, as <c>StripToMetadata</c> does. The memory rule keeps it: the
    /// message is a fixed string per action, not content.</param>
    public static void Withhold(JsonObject @event, bool message)
    {
        if (message)
            @event.Remove("message");
        RemovePath(@event, "prompt", "text");
        RemovePath(@event, "command", "output");
        RemovePath(@event, "approval", "reason");
        RemovePath(@event, "gen_ai", "input");
        RemovePath(@event, "gen_ai", "output");
        RemovePath(@event, "gen_ai", "tool", "call", "arguments");
        RemovePath(@event, "gen_ai", "tool", "call", "result");
        if (@event["content"] is JsonObject content)
        {
            var withheld = new JsonObject { ["retention"] = "metadata", ["included"] = false, ["redacted"] = true };
            if (content["truncated"] is { } truncated)
                withheld["truncated"] = truncated.DeepClone();
            if (content["hash"] is { } hash)
                withheld["hash"] = hash.DeepClone();
            if (content["bytes"] is { } bytes)
                withheld["bytes"] = bytes.DeepClone();
            @event["content"] = withheld;
        }
    }

    // What goes first when a line is over the event cap, largest and least needed first.
    private static readonly string[][] CapOrder =
    [
        ["gen_ai", "tool", "call", "result"],
        ["gen_ai", "tool", "call", "arguments"],
        ["command", "output"],
        ["prompt", "text"],
        ["gen_ai", "output"],
        ["approval", "reason"],
    ];

    /// <summary>
    /// Serializes <paramref name="event"/>, dropping retained content in <see cref="CapOrder"/> until the line is at most
    /// <paramref name="maxBytes"/> UTF-8 bytes. A drop sets <c>field_truncated</c> and <c>content.truncated</c>; hashes and
    /// sizes stay. Beacon only flags an oversized line; Corvid enforces the cap.
    /// </summary>
    public static string Cap(JsonObject @event, int maxBytes, Func<JsonNode, string> serialize)
    {
        var line = serialize(@event);
        foreach (var path in CapOrder)
        {
            if (Encoding.UTF8.GetByteCount(line) <= maxBytes)
                return line;
            if (!RemovePath(@event, path))
                continue;
            if (@event["content"] is JsonObject content)
                content["truncated"] = true;
            @event["field_truncated"] = true;
            line = serialize(@event);
        }
        return line;
    }

    /// <summary>Removes the last key of <paramref name="path"/>, then every object on the way that it left empty.</summary>
    public static bool RemovePath(JsonObject root, params string[] path)
    {
        var chain = new List<JsonObject> { root };
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (chain[^1][path[i]] is not JsonObject next)
                return false;
            chain.Add(next);
        }
        if (!chain[^1].Remove(path[^1]))
            return false;
        for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--)
            chain[i - 1].Remove(path[i - 1]);
        return true;
    }
}
