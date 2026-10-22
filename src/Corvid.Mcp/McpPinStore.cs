using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corvid.Mcp;

/// <summary>
/// One tool's pinned definition (C-022). <paramref name="Definition"/> is the canonical JSON the hash was taken over, kept
/// so a later change can be shown field by field; a hash alone can only say that something changed.
/// </summary>
/// <param name="AllowedFlagged">
/// The hash of a definition whose description matched a poisoning pattern and that the user allowed anyway (<c>/mcp allow</c>);
/// any other definition of the tool is flagged again. C-033: also the hash of a definition withheld after an
/// <c>injection</c> verdict that the user allowed; a later verdict over that definition is only logged.
/// </param>
/// <param name="Withheld">
/// C-033: the hash of a definition withheld after a confident <c>injection</c> verdict over its description. A start that
/// lists that definition holds it as <c>injection</c> again until the user allows it; written only when set, so a file
/// without withholds stays as C-022 wrote it.
/// </param>
/// <param name="WithheldDecision">The <c>Decision</c> that withheld it, for <c>/mcp diff</c> and the log.</param>
public sealed record McpToolPin(string Hash, string Definition, DateTimeOffset PinnedAt, string? AllowedFlagged = null,
    string? Withheld = null, string? WithheldDecision = null);

/// <summary>Where pinned tool definitions live, per configured server and tool name. Corvid state, not the user's configuration.</summary>
public interface IMcpPinStore
{
    /// <summary>The server's pins by tool name; empty when the server was never pinned. Throws <see cref="InvalidDataException"/> for an unreadable store.</summary>
    IReadOnlyDictionary<string, McpToolPin> Pins(string server);

    /// <summary>
    /// Whether the server has an entry, pins or not: a first listing records the server even when it pins nothing (every tool
    /// flagged, every name taken), so the next listing is checked against the pins instead of being trusted on first use again.
    /// Throws <see cref="InvalidDataException"/> for an unreadable store.
    /// </summary>
    bool Listed(string server);

    /// <summary>Adds or replaces the given tools' pins, and records the server even when <paramref name="pins"/> is empty; the server's other pins are kept.</summary>
    void Set(string server, IReadOnlyDictionary<string, McpToolPin> pins);

    /// <summary>
    /// Drops the server's entry, pins and all (C-033, <c>/mcp forget</c>), so its next listing is a first use again. Returns
    /// how many pins were dropped (0 for an entry that pinned nothing), or null when the server had no entry.
    /// </summary>
    int? Remove(string server);

    /// <summary>Every server with an entry, ordered ordinally (C-033: <c>corvid mcp forget --unconfigured</c>, <c>corvid mcp list</c>).</summary>
    IReadOnlyList<string> Servers();
}

public sealed class InMemoryMcpPinStore : IMcpPinStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ImmutableDictionary<string, McpToolPin>> _pins = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, McpToolPin> Pins(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        lock (_gate)
            return _pins.GetValueOrDefault(server) ?? ImmutableDictionary<string, McpToolPin>.Empty;
    }

    public bool Listed(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        lock (_gate)
            return _pins.ContainsKey(server);
    }

    public void Set(string server, IReadOnlyDictionary<string, McpToolPin> pins)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentNullException.ThrowIfNull(pins);
        lock (_gate)
            _pins[server] = (_pins.GetValueOrDefault(server) ?? ImmutableDictionary.Create<string, McpToolPin>(StringComparer.Ordinal)).SetItems(pins);
    }

    public int? Remove(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        lock (_gate)
            return _pins.Remove(server, out var removed) ? removed.Count : null;
    }

    public IReadOnlyList<string> Servers()
    {
        lock (_gate)
            return [.. _pins.Keys.Order(StringComparer.Ordinal)];
    }
}

/// <summary>
/// Pins in a JSON file (format <c>corvid-mcp-pins</c>, version 1), by default <c>mcp-pins.json</c> in the data directory,
/// which no tool and no sandboxed server may write. Every read goes to the file, and every write re-reads it and merges by
/// server and tool before replacing it atomically, so <c>corvid mcp list</c> and a running chat do not drop each other's pins.
/// </summary>
public sealed class JsonFileMcpPinStore : IMcpPinStore
{
    public const string Format = "corvid-mcp-pins";
    public const int FormatVersion = 1;

    private readonly string _path;
    private readonly Lock _gate = new();

    public JsonFileMcpPinStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public IReadOnlyDictionary<string, McpToolPin> Pins(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        lock (_gate)
            return Read().GetValueOrDefault(server) ?? (IReadOnlyDictionary<string, McpToolPin>)ImmutableDictionary<string, McpToolPin>.Empty;
    }

    public bool Listed(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        lock (_gate)
            return Read().ContainsKey(server);
    }

    public void Set(string server, IReadOnlyDictionary<string, McpToolPin> pins)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentNullException.ThrowIfNull(pins);
        lock (_gate)
        {
            var all = Read();
            var merged = all.GetValueOrDefault(server) is { } existing
                ? new SortedDictionary<string, McpToolPin>(existing.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal)
                : new SortedDictionary<string, McpToolPin>(StringComparer.Ordinal);
            foreach (var (tool, pin) in pins)
                merged[tool] = pin;
            all[server] = merged;
            Write(all);
        }
    }

    public int? Remove(string server)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        lock (_gate)
        {
            // The same re-read and atomic replace as Set, so another process's pins for other servers are kept.
            var all = Read();
            if (!all.Remove(server, out var removed))
                return null;
            Write(all);
            return removed.Count;
        }
    }

    public IReadOnlyList<string> Servers()
    {
        lock (_gate)
            return [.. Read().Keys];
    }

    private void Write(SortedDictionary<string, IReadOnlyDictionary<string, McpToolPin>> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, Serialize(all));
        File.Move(temporary, _path, overwrite: true);
    }

    private SortedDictionary<string, IReadOnlyDictionary<string, McpToolPin>> Read() =>
        File.Exists(_path) ? Parse(File.ReadAllText(_path)) : new(StringComparer.Ordinal);

    public static string Serialize(IReadOnlyDictionary<string, IReadOnlyDictionary<string, McpToolPin>> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        var servers = pins.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(tool => tool.Key, StringComparer.Ordinal).ToDictionary(
                tool => tool.Key,
                tool => new PinDocument(tool.Value.Hash, tool.Value.Definition, tool.Value.PinnedAt, tool.Value.AllowedFlagged,
                    tool.Value.Withheld, tool.Value.WithheldDecision),
                StringComparer.Ordinal),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(new PinFileDocument(Format, FormatVersion, servers), McpPinJsonContext.Default.PinFileDocument);
    }

    public static SortedDictionary<string, IReadOnlyDictionary<string, McpToolPin>> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        PinFileDocument? file;
        try
        {
            file = JsonSerializer.Deserialize(json, McpPinJsonContext.Default.PinFileDocument);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid MCP pin file.", exception);
        }
        if (file is null || file.Format != Format || file.FormatVersion != FormatVersion)
            throw new InvalidDataException($"Not a {Format} file of version {FormatVersion}.");
        var result = new SortedDictionary<string, IReadOnlyDictionary<string, McpToolPin>>(StringComparer.Ordinal);
        foreach (var (server, tools) in file.Servers)
            result[server] = tools.ToDictionary(pair => pair.Key, pair => pair.Value.Hash.Length == 64 && pair.Value.Definition.Length > 0
                    ? new McpToolPin(pair.Value.Hash, pair.Value.Definition, pair.Value.PinnedAt, pair.Value.AllowedFlagged,
                        pair.Value.Withheld, pair.Value.WithheldDecision)
                    : throw new InvalidDataException($"The pin of {server}/{pair.Key} has no SHA-256 hash or definition."),
                StringComparer.Ordinal);
        return result;
    }
}

// C-033's two fields are written only when set: a file without withholds is byte for byte what C-022 wrote, and one with a
// withhold is refused by an older Corvid (an unknown key), which then offers nothing from any server, i.e. it fails closed.
internal sealed record PinDocument(string Hash, string Definition, DateTimeOffset PinnedAt, string? AllowedFlagged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Withheld = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WithheldDecision = null);

internal sealed record PinFileDocument(string Format, int FormatVersion, Dictionary<string, Dictionary<string, PinDocument>> Servers);

// Unknown properties fail the load, as for approval rules: a misspelt key must not silently drop a pin.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = true)]
[JsonSerializable(typeof(PinFileDocument))]
internal sealed partial class McpPinJsonContext : JsonSerializerContext;
