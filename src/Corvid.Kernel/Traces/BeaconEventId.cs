using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Corvid.Kernel.Traces;

/// <summary>
/// Beacon's deterministic <c>event.id</c> (<c>pkg/asymptoteobserve/identity.go</c>, <c>EventIDForLine</c>), reproduced so
/// that a Corvid export names a tool, command, file or MCP event the way Beacon names the same action: an RFC 4122 version 5
/// UUID under Beacon's fixed namespace. An event with a tool call id and a target (<see cref="Target"/>) is named from
/// session, action, target and call id alone ("call"); any other event from its own serialized line without
/// <c>event.id</c> ("content"), which is stable across Corvid runs but not equal to what Beacon would compute from its own
/// byte layout of the same event.
/// </summary>
internal static class BeaconEventId
{
    // Beacon's eventIDNamespace. It must never change: changing it renames every event id ever written.
    private static readonly byte[] Namespace =
    [
        0x1f, 0x4b, 0x2c, 0x8e, 0x6d, 0x3a, 0x5a, 0x7c,
        0x9b, 0x61, 0x3f, 0x0d, 0x5e, 0x8c, 0x2a, 0x14,
    ];

    /// <summary>The id of an event whose serialized form is <paramref name="line"/> (<c>event.id</c> absent).</summary>
    public static string For(JsonObject @event, string line)
    {
        var action = Nested(@event, "event", "action");
        var callId = Nested(@event, "gen_ai", "tool", "call", "id");
        var target = Target(action, @event);
        if (callId.Length > 0 && target.Length > 0)
            return Uuid("call", string.Join('\0', Nested(@event, "session", "id"), action.Trim().ToLowerInvariant(), target, callId));
        return Uuid("content", line);
    }

    /// <summary>Beacon's <c>dedupeTarget</c>: what one action acted on, or empty for actions it has no target for.</summary>
    public static string Target(string action, JsonObject @event) => action.Trim().ToLowerInvariant() switch
    {
        "mcp.tool_invoked" => Canonical("mcp", Nested(@event, "mcp", "server"),
            FirstNonEmpty(Nested(@event, "mcp", "tool"), Nested(@event, "tool", "name"), Nested(@event, "message"))),
        "command.executed" => Canonical("command", Nested(@event, "command", "command"), Nested(@event, "tool", "command")),
        // The path alone: Beacon leaves file.operation out because two capture paths describe one write differently.
        "file.read" or "file.modified" => Canonical("file", Nested(@event, "file", "path")),
        "tool.completed" => Canonical("tool.completed", Nested(@event, "model"), Nested(@event, "message")),
        "tool.invoked" or "tool.failed" => Canonical("tool", Nested(@event, "tool", "name"), Nested(@event, "tool", "path"),
            Nested(@event, "message")),
        _ => "",
    };

    /// <summary>Beacon's <c>canonicalTarget</c>: each part trimmed and lowercased, MCP prefixes dropped, <c>__</c> as <c>:</c>.</summary>
    public static string Canonical(string prefix, params string[] values)
    {
        var parts = new List<string>();
        foreach (var raw in values)
        {
            var value = raw.Trim().ToLowerInvariant();
            if (value.StartsWith("mcp:", StringComparison.Ordinal))
                value = value[4..];
            if (value.StartsWith("mcp__", StringComparison.Ordinal))
                value = value[5..];
            value = value.Replace("__", ":", StringComparison.Ordinal);
            if (value.Length > 0 && value != "<nil>")
                parts.Add(value);
        }
        return parts.Count == 0 ? "" : prefix + ":" + string.Join('|', parts);
    }

    /// <summary>Beacon's <c>eventUUID</c>: SHA-1 over namespace, kind and name, with the version 5 and variant bits set.</summary>
    /// <remarks>SHA-1 is what RFC 4122 version 5 specifies; it names events and is no security boundary.</remarks>
    public static string Uuid(string kind, string name)
    {
        var kindBytes = Encoding.UTF8.GetBytes(kind);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[Namespace.Length + kindBytes.Length + nameBytes.Length];
        Namespace.CopyTo(input, 0);
        kindBytes.CopyTo(input, Namespace.Length);
        nameBytes.CopyTo(input, Namespace.Length + kindBytes.Length);
#pragma warning disable CA5350 // RFC 4122 version 5 is defined over SHA-1; this is an identifier, not a security boundary.
        var digest = SHA1.HashData(input);
#pragma warning restore CA5350
        digest[6] = (byte)((digest[6] & 0x0f) | 0x50);
        digest[8] = (byte)((digest[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexStringLower(digest.AsSpan(0, 16));
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    private static string Nested(JsonObject root, params string[] keys)
    {
        JsonNode? current = root;
        foreach (var key in keys)
        {
            if (current is not JsonObject next)
                return "";
            current = next[key];
        }
        return current is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim()
            : current is JsonValue other ? other.ToJsonString().Trim() : "";
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => value.Length > 0) ?? "";
}
