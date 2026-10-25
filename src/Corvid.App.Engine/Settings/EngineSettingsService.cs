using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Settings;
using Corvid.Host.Configuration;
using Corvid.Host.State;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Settings;

/// <summary>
/// Settings over the real <c>corvid.json</c> (docs/plan/ui.md F24): the typed page and the raw editor read the file as it
/// is now and write it through the host's <see cref="CorvidConfigWriter"/>, which validates the result as the next start
/// would read it and replaces the file only when that passes. The runtime reads <c>corvid.json</c> once, at start, so every
/// change to the file applies after a restart, and every <c>corvid.json</c> setting is tagged so. Values the environment or
/// <c>--set</c> give this run are shown as set for this run. The app's own preferences (density, notifications, layout) are
/// kept in <c>ui.db</c> (G-7), beside the event log, so they last across restarts; the window applies the layout ones when
/// it starts.
/// </summary>
public sealed class EngineSettingsService : ISettingsService
{
    private const string Skeleton = "{\n  \"Corvid\": {\n  }\n}\n";

    private readonly CorvidConfigWriter _writer;
    private readonly UiStateStore _ui;
    private readonly TimeProvider _time;

    public EngineSettingsService(AppEngine engine, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _writer = engine.Services.GetRequiredService<CorvidConfigWriter>();
        _ui = engine.Services.GetRequiredService<UiStateStore>();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Where the app's own preferences are kept: ui.db, shared with the CLI and kept across restarts (G-7).</summary>
    public string PreferencesNote => "saved in ui.db · kept across restarts";

    /// <summary>The catalogue's settings; every <c>corvid.json</c> one applies after a restart, since the runtime never reloads the file.</summary>
    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        .. SettingsCatalogue.Definitions.Select(definition =>
            definition.Store == SettingStore.CorvidJson ? definition with { Applies = SettingApplies.Restart } : definition),
    ];

    public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = File.Exists(_writer.Path) ? File.ReadAllText(_writer.Path) : Skeleton;
        // A file that does not parse still opens: its text goes to the raw editor, whose problems say where, and the typed
        // page shows the defaults until it is fixed.
        var values = SettingsJson.TryRead(SettingsJson.Lines(text), out var root, out _) ? Values(root!) : Values(null);
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Definitions.Where(definition => definition.Store == SettingStore.CorvidJson))
            if (_writer.TryGetOverride(definition.FullKey, out var value) && value is not null)
                overrides[definition.Key] = value;
        return new SettingsSnapshot(_writer.Path, text, values, overrides, IsDemo: false);
    }, cancellationToken);

    public IReadOnlyList<SettingProblem> Validate(IReadOnlyDictionary<string, string> values) => SettingsCatalogue.Validate(values);

    public SettingsParse Parse(string json)
    {
        json = SettingsJson.Lines(json);
        if (!SettingsJson.TryRead(json, out var root, out var problem))
            return new SettingsParse(new Dictionary<string, string>(), [problem!]);
        var values = Values(root!);
        var problems = new List<SettingProblem>(SettingsJson.Hidden(json, root!));
        problems.AddRange(SettingsCatalogue.Validate(values));
        if (problems.Count == 0)
        {
            // The host's own validator over the whole file, as the next start would read it: it knows the parts the typed
            // page does not show (remote models, MCP servers, the gateway). Nothing is written.
            var check = _writer.Replace(json, write: false);
            problems.AddRange(check.Failures.Select(failure => new SettingProblem(KeyOf(failure), SafeText.Clean(failure))));
        }

        return new SettingsParse(values, [.. problems.DistinctBy(item => item.Message)]);
    }

    public Task<SettingsWrite> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = CurrentValues();
            var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
            foreach (var (key, value) in values)
                merged[key] = value;
            var problems = SettingsCatalogue.Validate(merged);
            if (problems.Count > 0)
                return Refused(problems);

            var edits = new List<ConfigEdit>();
            var app = new List<SettingDefinition>();
            foreach (var (key, value) in values)
            {
                var definition = Definitions.First(item => string.Equals(item.Key, key, StringComparison.Ordinal));
                if (string.Equals(current.GetValueOrDefault(key), value, StringComparison.Ordinal))
                    continue;
                if (definition.Store == SettingStore.App)
                    app.Add(definition);
                else
                    edits.Add(new ConfigEdit(definition.FullKey, SettingsJson.Node(definition, value)));
            }

            ConfigWriteResult? result = null;
            if (edits.Count > 0)
            {
                result = _writer.Apply(edits);
                if (!result.Written)
                    return Refused([.. result.Failures.Select(failure => new SettingProblem(KeyOf(failure), SafeText.Clean(failure)))]);
            }

            // G-7: written in one transaction, so another process reads all of them or none.
            if (app.Count > 0)
                _ui.SetPreferences(app.ToDictionary(definition => definition.Key, definition => values[definition.Key].Trim(), StringComparer.Ordinal),
                    _time.GetUtcNow());

            return Saved(result, app.Count > 0);
        }, cancellationToken);
    }

    public Task<SettingsWrite> SaveJsonAsync(string json, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        json = SettingsJson.Lines(json);
        var parse = Parse(json);
        if (!parse.IsValid)
            return Refused(parse.Problems);
        var result = _writer.Replace(json);
        return result.Written
            ? Saved(result, app: false)
            : Refused([.. result.Failures.Select(failure => new SettingProblem(KeyOf(failure), SafeText.Clean(failure)))]);
    }, cancellationToken);

    /// <summary>The typed values the file holds now (the defaults for settings it leaves out, or when it does not parse).</summary>
    private Dictionary<string, string> CurrentValues()
    {
        var text = File.Exists(_writer.Path) ? File.ReadAllText(_writer.Path) : Skeleton;
        return SettingsJson.TryRead(SettingsJson.Lines(text), out var root, out _) ? Values(root!) : Values(null);
    }

    private Dictionary<string, string> Values(JsonObject? root)
    {
        var corvid = root is null ? null : SettingsJson.Corvid(root);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var app = _ui.Preferences();
        foreach (var definition in Definitions)
        {
            values[definition.Key] = definition.Store == SettingStore.App
                ? app.GetValueOrDefault(definition.Key, definition.Default)
                : SettingsJson.Read(corvid, definition) ?? definition.Default;
        }

        return values;
    }

    /// <summary>The setting a validator's message names ("Corvid:Turn:MaxSteps must be …"), the longest match; null for the whole file.</summary>
    private string? KeyOf(string failure) =>
        Definitions.Where(definition => definition.Store == SettingStore.CorvidJson
                && failure.StartsWith(definition.FullKey, StringComparison.OrdinalIgnoreCase)
                && (failure.Length == definition.FullKey.Length || !char.IsLetterOrDigit(failure[definition.FullKey.Length])))
            .OrderByDescending(definition => definition.FullKey.Length)
            .Select(definition => definition.Key)
            .FirstOrDefault();

    private static SettingsWrite Refused(IReadOnlyList<SettingProblem> problems)
    {
        var head = problems.Count == 1 ? "Not saved: the validator found a problem." : $"Not saved: the validator found {problems.Count} problems.";
        // The raw editor shows only the message of a refusal, so the problems are in it too.
        return new SettingsWrite(false, $"{head} {string.Join(" ", problems.Select(problem => problem.Message))}".Trim(), problems, []);
    }

    private SettingsWrite Saved(ConfigWriteResult? result, bool app)
    {
        var parts = new List<string>();
        var restart = new List<string>();
        if (result is not null)
        {
            var changed = Definitions.Where(definition => definition.Store == SettingStore.CorvidJson
                && result.RestartKeys.Contains(definition.FullKey, StringComparer.OrdinalIgnoreCase)).ToList();
            restart.AddRange(changed.Select(definition => definition.Key));
            if (result.RestartKeys.IsEmpty)
            {
                parts.Add("Nothing in corvid.json changed.");
            }
            else
            {
                parts.Add($"Written to {SafeText.Clean(result.Path)}.");
                parts.Add("Corvid reads corvid.json when it starts, so the change applies after a restart.");
            }

            var shadowed = result.ShadowedKeys.Select(key => SafeText.Clean(key)).ToList();
            if (shadowed.Count > 0)
                parts.Add($"This run's environment or --set overrides {string.Join(", ", shadowed)}, so it goes on using that value.");
        }

        if (app)
            parts.Add("App preferences saved in ui.db.");
        if (parts.Count == 0)
            parts.Add("Nothing changed.");
        return new SettingsWrite(true, string.Join(" ", parts), [], restart);
    }
}
