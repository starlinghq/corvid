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
/// half a file and a refused edit leaves it byte for byte as it was.
/// <para>
/// An edit changes only the text of the keys it names: the file's comments, layout and key order stay as they were
/// (<see cref="JsoncText"/>). The replacement keeps the file's Unix permissions (a new file is the owner's alone, 0600), and
/// a symbolic link is written through to its target, which stays a link. Writes are serialized within a process and,
/// through an advisory lock file beside the file, between processes; a file changed by something else between the read
/// and the replacement (an editor) is refused rather than overwritten.
/// </para>
/// </summary>
public sealed class CorvidConfigWriter(CorvidConfigFile file, IConfiguration configuration, IValidateOptions<CorvidOptions> validator)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>The file this writer edits.</summary>
    public string Path => file.Path;

    /// <summary>How long a write waits for another process's write to finish before it is refused.</summary>
    internal TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Test seam: runs after the new text is validated, just before the file is checked for changes and replaced.</summary>
    internal Action? BeforeReplace { get; set; }

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
            string? busy = null;
            using var held = write ? Lock(out busy) : null;
            if (write && held is null)
                return Refused(busy!);
            JsonObject document;
            try
            {
                document = ParseObject(json);
            }
            catch (JsonException exception)
            {
                return Refused($"The text is not valid JSON: {exception.Message}");
            }
            var original = ReadText();
            var before = Effective(ReadOrEmpty(out _));
            return Finish(document, json, Changed(before, document), Effective(document).GetChildren().Select(child => child.Path), write, original);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The configuration the next start of this process would read: the file as it is now (an empty object when it does
    /// not exist), with this process's environment and <c>--set</c> values over it. The host's own configuration is read
    /// once, at start, and the runtime never reloads, so after a write only this shows what was written (an MCP server's
    /// trust, a server added, a slot's model).
    /// </summary>
    /// <exception cref="InvalidDataException">The file cannot be read as JSON.</exception>
    public IConfiguration NextStart()
    {
        Gate.Wait();
        try
        {
            string text;
            try
            {
                text = File.Exists(file.Path) ? File.ReadAllText(file.Path) : "{}";
                return Candidate(text);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"{file.Path} cannot be read as JSON: {exception.Message}", exception);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// The value a source over the file gives <paramref name="key"/> for this run (an environment variable, <c>--set</c>):
    /// the file can say otherwise, but this process, and the next start with the same environment, sees this one.
    /// </summary>
    public bool TryGetOverride(string key, out string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        value = null;
        if (configuration is not IConfigurationRoot root)
            return false;
        var providers = root.Providers.ToList();
        var index = providers.FindIndex(provider => provider is JsonConfigurationProvider json && IsThisFile(json.Source));
        if (index < 0)
            return false;
        // The last source that has the key wins, as configuration reads it.
        for (var i = providers.Count - 1; i > index; i--)
            if (providers[i].TryGet(key, out value))
                return true;
        value = null;
        return false;
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
            string? busy = null;
            using var held = write ? Lock(out busy) : null;
            if (write && held is null)
                return Refused(busy!);
            var original = ReadText();
            var current = ReadOrEmpty(out var problem);
            if (problem is not null)
                return Refused(problem);
            var before = Effective(current);
            // The file's own text is edited, so its comments and layout stay; a missing file starts as an empty object.
            var text = original ?? "{}";
            JsonObject document;
            try
            {
                foreach (var edit in edits)
                    text = JsoncText.Set(text, edit.Key.Split(':'), edit.Value?.DeepClone());
                document = ParseObject(text);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                return Refused(exception.Message);
            }
            // Only the keys asked for count as changed: a key whose value is what it was needs nothing restarted.
            var after = Effective(document);
            var changed = edits.Select(edit => edit.Key)
                .Where(key => !SameSection(before, after, key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            return Finish(document, text, changed, edits.Where(edit => edit.Value is not null).Select(edit => edit.Key), write, original);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static JsonObject ParseObject(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new JsonException("The configuration must be one JSON object.");

    /// <summary>The file's text now; null when it does not exist.</summary>
    private string? ReadText()
    {
        try
        {
            return File.Exists(file.Path) ? File.ReadAllText(file.Path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null; // ReadOrEmpty says what is wrong with it
        }
    }

    /// <summary>
    /// Validates the edited file twice and writes it when both pass: as the next start of this process would read it (the
    /// environment and <c>--set</c> over the file), and with the written values on top, as a start without those overrides
    /// would see them. So neither an override that hides a bad value nor one that fixes it decides alone.
    /// </summary>
    /// <param name="written">The keys whose file values are validated on top: the edited ones, or the whole file.</param>
    /// <param name="original">The file's text when it was read for this edit (null when there was none): the file must still hold it.</param>
    private ConfigWriteResult Finish(JsonObject document, string text, ImmutableArray<string> changed, IEnumerable<string> written, bool write,
        string? original)
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
        {
            BeforeReplace?.Invoke();
            // Something that does not take the lock (an editor) changed the file since it was read: its change would be lost.
            if (!string.Equals(ReadText(), original, StringComparison.Ordinal))
                return Refused($"{file.Path} changed while this edit was being made, so it was not replaced. Nothing was written; make the change again.");
            try
            {
                WriteAtomically(text);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Refused($"{file.Path} could not be written: {exception.Message}");
            }
        }
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

    /// <summary>
    /// Takes the advisory lock beside the file (<c>.corvid.json.lock</c>), which every writer of the file holds while it
    /// reads, validates and replaces it: an exclusive open, which is <c>flock</c> on Unix and a share mode on Windows. Null,
    /// with why, when another writer holds it past <see cref="LockTimeout"/>.
    /// </summary>
    private FileStream? Lock(out string? busy)
    {
        busy = null;
        var target = Target();
        var directory = System.IO.Path.GetDirectoryName(target)!;
        var path = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(target)}.lock");
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return new FileStream(path, options);
            }
            catch (IOException) when (waited.Elapsed < LockTimeout)
            {
                Thread.Sleep(LockPoll);
            }
            catch (IOException exception)
            {
                busy = $"Another Corvid process is writing {file.Path} (it holds {path}), so nothing was written; try again in a moment. ({exception.Message})";
                return null;
            }
            catch (UnauthorizedAccessException exception)
            {
                busy = $"{path} cannot be opened to lock {file.Path}, so nothing was written: {exception.Message}";
                return null;
            }
        }
    }

    /// <summary>The file a write replaces: the configuration file, or what its symbolic link (followed to the end) names.</summary>
    private string Target()
    {
        var full = System.IO.Path.GetFullPath(file.Path);
        try
        {
            return new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full; // a link loop: the write fails on the link itself and says so
        }
    }

    private void WriteAtomically(string text)
    {
        // Through a symbolic link to its target, so the link stays a link.
        var target = Target();
        var directory = System.IO.Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        // Beside the file, so the rename stays on one filesystem and replaces it in one step.
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            UnixFileMode? mode = null;
            if (!OperatingSystem.IsWindows())
            {
                // The file keeps its permissions; a new one is the owner's alone. The umask narrows a created file's mode, so
                // it is set again once the file exists.
                mode = File.Exists(target) ? File.GetUnixFileMode(target) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
                options.UnixCreateMode = mode.Value & (UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            using (var stream = new FileStream(temporary, options))
            {
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (mode is { } kept && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, kept);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
