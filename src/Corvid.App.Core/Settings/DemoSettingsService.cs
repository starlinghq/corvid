using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Corvid.App.Core.Settings;

/// <summary>
/// The demo's configuration: an in-memory <c>corvid.json</c> that matches the demo scenario (the generator ladder Models
/// shows, the Telegram adapter whose pairing waits in the tray, the notes MCP server whose <c>notes__search</c> is held,
/// the nightly-tidy job's scheduler) and one <c>--set</c> override (<c>Turn:MaxSteps=6</c>, the "step 2 of 6" the main
/// session shows). Saving validates and then changes only this copy: no file is read or written, and the page says so.
/// </summary>
public sealed class DemoSettingsService : ISettingsService
{
    /// <summary>Where the engine's file would be; shown, never opened.</summary>
    public const string DemoPath = "~/.config/corvid/corvid.json";

    // Written for a person to read and edit: "+" and non-ASCII stay as they are rather than \u escapes.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly Dictionary<string, string> _app = new(StringComparer.Ordinal);
    private JsonObject _root;

    public DemoSettingsService()
    {
        _root = (JsonObject)JsonNode.Parse(InitialJson, documentOptions: Reading)!;
        foreach (var definition in SettingsCatalogue.Definitions.Where(definition => definition.Store == SettingStore.App))
            _app[definition.Key] = definition.Default;
    }

    public IReadOnlyList<SettingDefinition> Definitions => SettingsCatalogue.Definitions;

    /// <summary>The values given with <c>--set</c> for this run.</summary>
    public static IReadOnlyDictionary<string, string> RunOverrides { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Turn:MaxSteps"] = "6",
    };

    public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SettingsSnapshot(DemoPath, _root.ToJsonString(Indented), Values(_root), RunOverrides, IsDemo: true));
    }

    public IReadOnlyList<SettingProblem> Validate(IReadOnlyDictionary<string, string> values) => SettingsCatalogue.Validate(values);

    public SettingsParse Parse(string json)
    {
        json = Lines(json);
        if (!TryRead(json, out var root, out var problem))
            return new SettingsParse(new Dictionary<string, string>(), [problem!]);
        var values = Values(root!);
        var problems = new List<SettingProblem>();
        var line = HiddenLine(json);
        if (line > 0)
            problems.Add(new SettingProblem(null, $"Line {line.ToString(CultureInfo.InvariantCulture)} holds hidden characters: " +
                $"{SafeText.Reveal(json.Split('\n')[line - 1].Trim())}. Remove them before saving."));
        problems.AddRange(SettingsCatalogue.Validate(values));
        return new SettingsParse(values, problems);
    }

    public Task<SettingsWrite> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var current = Values(_root);
        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (key, value) in values)
            merged[key] = value;
        var problems = SettingsCatalogue.Validate(merged);
        if (problems.Count > 0)
            return Task.FromResult(Refused(problems));

        var root = (JsonObject)_root.DeepClone();
        var changed = new List<SettingDefinition>();
        foreach (var (key, value) in values)
        {
            var definition = SettingsCatalogue.Find(key);
            if (string.Equals(current.GetValueOrDefault(key), value, StringComparison.Ordinal))
                continue;
            changed.Add(definition);
            if (definition.Store == SettingStore.App)
                continue;
            Write(root, definition, value.Trim());
        }

        _root = root;
        foreach (var definition in changed.Where(definition => definition.Store == SettingStore.App))
            _app[definition.Key] = values[definition.Key].Trim();
        return Task.FromResult(Saved(changed));
    }

    public Task<SettingsWrite> SaveJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        json = Lines(json);
        var parse = Parse(json);
        if (!parse.IsValid || !TryRead(json, out var root, out _))
            return Task.FromResult(Refused(parse.Problems));
        var before = Values(_root);
        _root = root!;
        var changed = SettingsCatalogue.Definitions
            .Where(definition => definition.Store == SettingStore.CorvidJson
                && !string.Equals(before.GetValueOrDefault(definition.Key), parse.Values.GetValueOrDefault(definition.Key), StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(Saved(changed));
    }

    private static SettingsWrite Refused(IReadOnlyList<SettingProblem> problems) =>
        new(false, problems.Count == 1 ? "Not saved: the validator found a problem." : $"Not saved: the validator found {problems.Count} problems.", problems, []);

    private static SettingsWrite Saved(IReadOnlyList<SettingDefinition> changed)
    {
        var restart = changed.Where(definition => definition.Applies == SettingApplies.Restart).Select(definition => definition.Key).ToList();
        var message = changed.Count == 0
            ? "Nothing changed."
            : "Kept in the demo's in-memory copy of corvid.json; nothing was written to disk.";
        if (restart.Count > 0)
            message += restart.Count == 1 ? $" {restart[0]} takes effect after a restart." : $" {string.Join(", ", restart)} take effect after a restart.";
        return new SettingsWrite(true, message, [], restart);
    }

    private Dictionary<string, string> Values(JsonObject root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var corvid = root["Corvid"] as JsonObject;
        foreach (var definition in SettingsCatalogue.Definitions)
        {
            values[definition.Key] = definition.Store == SettingStore.App
                ? _app.GetValueOrDefault(definition.Key, definition.Default)
                : Read(corvid, definition) ?? definition.Default;
        }

        return values;
    }

    private static string? Read(JsonObject? corvid, SettingDefinition definition)
    {
        JsonNode? node = corvid;
        foreach (var part in definition.Key.Split(':'))
            node = node is JsonObject obj && obj.TryGetPropertyValue(part, out var child) ? child : null;
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(),
        };
    }

    private static void Write(JsonObject root, SettingDefinition definition, string value)
    {
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
        if (value.Length == 0 && definition.Kind is SettingKind.Path or SettingKind.Url or SettingKind.Text)
        {
            parent.Remove(name);
            return;
        }

        parent[name] = definition.Kind switch
        {
            SettingKind.Toggle => JsonValue.Create(bool.Parse(value)),
            SettingKind.Number when long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole) => JsonValue.Create(whole),
            SettingKind.Number => JsonValue.Create(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(value),
        };
    }

    private static bool TryRead(string json, out JsonObject? root, out SettingProblem? problem)
    {
        root = null;
        problem = null;
        try
        {
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
    }

    /// <summary>A text box hands back its line breaks as carriage returns; they are line breaks, not hidden characters.</summary>
    public static string Lines(string? json) => (json ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

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

    private const string InitialJson = """
        {
          "Corvid": {
            "Workspace": "~/src/corvid",
            "Models": {
              "Slots": {
                "generator": { "Model": "qwen3.5-0.8b", "Escalation": [ "qwen-27b", "claude" ] },
                "decider": { "Model": "kev-0.8b", "Escalation": [ "kev-4b" ] }
              },
              "Placement": { "kev-0.8b": { "Device": "cuda:0", "Fallback": "Cpu" } }
            },
            "Devices": { "CpuThreads": 4 },
            "Approvals": { "Mode": "Smart", "TimeoutSeconds": 300 },
            "Turn": { "MaxSteps": 8, "MaxOutputTokens": 1024 },
            "Privacy": { "Cloud": "Redact", "Tainted": "Redact" },
            "Scheduler": { "Enabled": true },
            "Mcp": {
              "Servers": {
                "notes": { "Transport": "stdio", "Command": "notes-mcp", "Trust": "Untrusted" }
              }
            },
            "Gateway": {
              "Adapters": {
                "telegram": { "Enabled": true, "Secret": "telegram.bot", "Owners": [ "owner" ] }
              }
            }
          }
        }
        """;
}
