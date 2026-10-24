using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Configuration;

/// <summary>
/// One change to <c>corvid.json</c>: a configuration key (<c>Corvid:Models:Slots:generator:Model</c>, any case, as the
/// configuration reads it) and the JSON it gets. A null value removes the key. An array is set whole
/// (<c>Corvid:Models:Slots:generator:Escalation</c> to <c>["a","b"]</c>), not element by element.
/// </summary>
public sealed record ConfigEdit(string Key, JsonNode? Value)
{
    /// <summary>A string value.</summary>
    public static ConfigEdit Set(string key, string value) => new(key, JsonValue.Create(value));

    /// <summary>Removes the key, so the default (or another source) applies.</summary>
    public static ConfigEdit Remove(string key) => new(key, null);
}

/// <summary>What <see cref="CorvidConfigWriter.Apply"/> did.</summary>
/// <param name="Written">The file was replaced; false when validation (or the file itself) refused the edits, and nothing changed.</param>
/// <param name="Path">The configuration file.</param>
/// <param name="Failures">Why the edits were refused: the validator's messages, or the file's problem.</param>
/// <param name="RestartKeys">
/// The edited keys whose value changed. The runtime reads <c>corvid.json</c> once, at start (no reload), so each applies
/// from the next start of a runtime; a caller that applies one at once (the Models page binding a slot) says so itself.
/// </param>
/// <param name="ShadowedKeys">
/// Edited keys another source overrides for this run (an environment variable, <c>--set</c>): the file has the new value,
/// but this process goes on seeing the other.
/// </param>
public sealed record ConfigWriteResult(bool Written, string Path, ImmutableArray<string> Failures, ImmutableArray<string> RestartKeys,
    ImmutableArray<string> ShadowedKeys)
{
    /// <summary>The failures as one paragraph, for a notice.</summary>
    public string FailureText => string.Join(" ", Failures);
}

/// <summary>
/// Edits the user's <c>corvid.json</c> the way <c>corvid model switch</c> always has, for every caller that changes the
/// configuration (the CLI, the windowed app's Models, First run, Settings, MCP and Gateway pages): the edits are applied to
/// the file's JSON in memory, the result is validated with <see cref="CorvidOptionsValidator"/> as the next start would
/// read it (the edited file in place of the current one, with this process's environment and <c>--set</c> values over it),
/// and only then is the file replaced, through a temporary file in the same directory and a rename, so a reader never sees
/// half a file and a refused edit leaves it byte for byte as it was. Writes from one process are serialized.
/// </summary>
public sealed class CorvidConfigWriter(CorvidConfigFile file, IConfiguration configuration, IValidateOptions<CorvidOptions> validator)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The file this writer edits.</summary>
    public string Path => file.Path;

    /// <summary>The file's JSON as it is now; an empty object when it does not exist.</summary>
    /// <exception cref="JsonException">The file is not a JSON object.</exception>
    public JsonObject Read()
    {
        if (!File.Exists(file.Path))
            return [];
        return JsonNode.Parse(File.ReadAllText(file.Path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new JsonException($"{file.Path} does not hold a JSON object.");
    }

    /// <summary>Validates the edits applied together and, when they pass, writes the file.</summary>
    public ConfigWriteResult Apply(params IReadOnlyList<ConfigEdit> edits) => Apply(edits, write: true);

    /// <summary>Validates the edits applied together without writing anything: what <see cref="Apply"/> would say.</summary>
    public ConfigWriteResult Check(params IReadOnlyList<ConfigEdit> edits) => Apply(edits, write: false);

    /// <summary>
    /// Replaces the whole file with <paramref name="json"/> (the raw editor), validated first: its text is kept as given,
    /// comments and layout included.
    /// </summary>
    public ConfigWriteResult Replace(string json, bool write = true)
    {
        ArgumentNullException.ThrowIfNull(json);
        Gate.Wait();
        try
        {
            JsonObject document;
            try
            {
                document = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    as JsonObject ?? throw new JsonException("The configuration must be one JSON object.");
            }
            catch (JsonException exception)
            {
                return Refused($"The text is not valid JSON: {exception.Message}");
            }
            var before = Effective(ReadOrEmpty(out _));
            return Finish(document, json, Changed(before, document), Effective(document).GetChildren().Select(child => child.Path), write);
        }
        finally
        {
            Gate.Release();
        }
    }

    private ConfigWriteResult Apply(IReadOnlyList<ConfigEdit> edits, bool write)
    {
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var edit in edits)
            if (string.IsNullOrWhiteSpace(edit.Key) || edit.Key.Split(':').Any(string.IsNullOrWhiteSpace))
                return Refused($"'{edit.Key}' is not a configuration key.");
        Gate.Wait();
        try
        {
            var document = ReadOrEmpty(out var problem);
            if (problem is not null)
                return Refused(problem);
            var before = Effective(document.DeepClone().AsObject());
            foreach (var edit in edits)
                Set(document, edit.Key.Split(':'), edit.Value?.DeepClone());
            // Only the keys asked for count as changed: a key whose value is what it was needs nothing restarted.
            var changed = edits.Select(edit => edit.Key)
                .Where(key => !SameSection(before, Effective(document), key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            return Finish(document, document.ToJsonString(Indented), changed, edits.Where(edit => edit.Value is not null).Select(edit => edit.Key), write);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Validates the edited file twice and writes it when both pass: as the next start of this process would read it (the
    /// environment and <c>--set</c> over the file), and with the written values on top, as a start without those overrides
    /// would see them. So neither an override that hides a bad value nor one that fixes it decides alone.
    /// </summary>
    /// <param name="written">The keys whose file values are validated on top: the edited ones, or the whole file.</param>
    private ConfigWriteResult Finish(JsonObject document, string text, ImmutableArray<string> changed, IEnumerable<string> written, bool write)
    {
        var fileOnly = Effective(document);
        var nextStart = Candidate(text);
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in written)
            foreach (var pair in Flatten(fileOnly, key))
                overlay[pair.Key] = pair.Value;
        var asWritten = new ConfigurationBuilder().AddConfiguration(nextStart).AddInMemoryCollection(overlay).Build();

        var failures = new List<string>();
        foreach (var candidate in new[] { nextStart, asWritten })
        {
            try
            {
                var options = candidate.GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();
                if (validator.Validate(null, options) is { Failed: true } validation)
                    failures.AddRange(validation.Failures ?? []);
            }
            catch (InvalidOperationException exception)
            {
                // A value of the wrong shape ("many" for a number): binding says which key.
                failures.Add(exception.Message);
            }
        }
        if (failures.Count > 0)
            return new ConfigWriteResult(false, file.Path, [.. failures.Distinct(StringComparer.Ordinal)], [], []);

        var shadowed = changed.Where(key => !SameSection(fileOnly, nextStart, key)).ToImmutableArray();
        if (write)
            WriteAtomically(text);
        return new ConfigWriteResult(write, file.Path, [], changed, shadowed);
    }

    /// <summary>A key's value and everything under it, with full keys.</summary>
    private static IEnumerable<KeyValuePair<string, string?>> Flatten(IConfiguration configuration, string key) =>
        configuration.GetSection(key).AsEnumerable().Where(pair => pair.Value is not null);

    private ConfigWriteResult Refused(string failure) => new(false, file.Path, [failure], [], []);

    private JsonObject ReadOrEmpty(out string? problem)
    {
        problem = null;
        try
        {
            return Read();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"{file.Path} cannot be read as JSON: {exception.Message}";
            return [];
        }
    }

    /// <summary>
    /// The configuration the next start would see: this process's sources in their order, with the edited text in place of
    /// the configuration file's provider. Environment variables and <c>--set</c> values (the in-memory overrides) still win.
    /// </summary>
    private IConfiguration Candidate(string text)
    {
        var builder = new ConfigurationBuilder();
        if (configuration is not IConfigurationRoot root)
            return builder.AddJsonStream(Stream(text)).AddConfiguration(configuration).Build();
        var replaced = false;
        foreach (var provider in root.Providers)
        {
            if (!replaced && provider is JsonConfigurationProvider json && IsThisFile(json.Source))
            {
                builder.AddJsonStream(Stream(text));
                replaced = true;
            }
            else
            {
                // A copy of what the source holds now, so the candidate shares no provider (or its reload tokens) with the host.
                builder.AddInMemoryCollection(Snapshot(provider));
            }
        }
        if (replaced)
            return builder.Build();
        // A configuration without the file among its sources: the file first, then everything else over it.
        var rebuilt = new ConfigurationBuilder().AddJsonStream(Stream(text));
        foreach (var source in builder.Sources)
            rebuilt.Add(source);
        return rebuilt.Build();
    }

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string?> Snapshot(IConfigurationProvider provider)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Walk(string? parent)
        {
            foreach (var key in provider.GetChildKeys([], parent).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = parent is null ? key : parent + ConfigurationPath.KeyDelimiter + key;
                if (provider.TryGet(path, out var value))
                    data[path] = value;
                Walk(path);
            }
        }
        Walk(null);
        return data;
    }

    private bool IsThisFile(FileConfigurationSource source)
    {
        if (source.Path is not { } path)
            return false;
        var full = source.FileProvider?.GetFileInfo(path).PhysicalPath ?? path;
        return string.Equals(System.IO.Path.GetFullPath(full), System.IO.Path.GetFullPath(file.Path), StringComparison.Ordinal);
    }

    private static IConfiguration Effective(JsonObject document) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(document.ToJsonString()))).Build();

    /// <summary>Whether a key (and everything under it) reads the same in both configurations.</summary>
    private static bool SameSection(IConfiguration left, IConfiguration right, string key)
    {
        static List<KeyValuePair<string, string?>> Normalized(IConfiguration configuration, string key) =>
        [
            .. Flatten(configuration, key)
                .Select(pair => new KeyValuePair<string, string?>(pair.Key.ToUpperInvariant(), pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal),
        ];
        return Normalized(left, key).SequenceEqual(Normalized(right, key));
    }

    private static ImmutableArray<string> Changed(IConfiguration before, JsonObject after)
    {
        var next = Effective(after);
        var keys = before.AsEnumerable().Concat(next.AsEnumerable()).Where(pair => pair.Value is not null).Select(pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return [.. keys.Where(key => !string.Equals(before[key], next[key], StringComparison.Ordinal)).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Sets (or with a null value removes) the node at a key's path, making the objects on the way.</summary>
    private static void Set(JsonObject document, string[] path, JsonNode? value)
    {
        JsonNode parent = document;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var child = Child(parent, path[i]);
            if (child is JsonObject or JsonArray)
            {
                parent = child;
                continue;
            }
            if (value is null)
                return; // nothing to remove below a key that is absent or a plain value
            var created = new JsonObject();
            Assign(parent, path[i], created);
            parent = created;
        }
        if (value is null)
            Remove(parent, path[^1]);
        else
            Assign(parent, path[^1], value);
    }

    private static JsonNode? Child(JsonNode parent, string key) => parent switch
    {
        // Configuration keys ignore case, so an existing "corvid" or "models" object is the one to extend.
        JsonObject obj => obj.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value,
        JsonArray array when int.TryParse(key, out var index) && index >= 0 && index < array.Count => array[index],
        _ => null,
    };

    private static void Assign(JsonNode parent, string key, JsonNode value)
    {
        switch (parent)
        {
            case JsonObject obj:
                var existing = obj.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Key;
                obj[existing ?? key] = value;
                break;
            case JsonArray array when int.TryParse(key, out var index) && index >= 0 && index <= array.Count:
                if (index == array.Count)
                    array.Add(value);
                else
                    array[index] = value;
                break;
            default:
                throw new InvalidOperationException($"'{key}' cannot be set inside an array except by its index.");
        }
    }

    private static void Remove(JsonNode parent, string key)
    {
        switch (parent)
        {
            case JsonObject obj:
                if (obj.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Key is { } existing)
                    obj.Remove(existing);
                break;
            case JsonArray array when int.TryParse(key, out var index) && index >= 0 && index < array.Count:
                array.RemoveAt(index);
                break;
        }
    }

    private void WriteAtomically(string text)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file.Path))!;
        Directory.CreateDirectory(directory);
        // Beside the file, so the rename stays on one filesystem and replaces it in one step.
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(file.Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file.Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
