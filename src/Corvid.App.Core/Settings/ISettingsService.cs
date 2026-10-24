namespace Corvid.App.Core.Settings;

/// <summary>
/// The Settings area's seam to the engine (docs/plan/ui.md F24): the typed settings over <c>corvid.json</c>, the file's raw
/// text, the host validator's messages, and saving. Every save goes through the validator first and writes nothing when
/// it finds a problem. <c>Corvid.App.Engine</c> will implement it over <c>CorvidOptions</c>, <c>CorvidOptionsValidator</c>
/// and the configuration file; the demo (<see cref="DemoSettingsService"/>) edits an in-memory copy and writes no file.
/// </summary>
public interface ISettingsService
{
    /// <summary>The typed settings, in the page's order.</summary>
    IReadOnlyList<SettingDefinition> Definitions { get; }

    Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>The validator's messages for these values (every typed setting, as text), all at once.</summary>
    IReadOnlyList<SettingProblem> Validate(IReadOnlyDictionary<string, string> values);

    /// <summary>Reads a raw <c>corvid.json</c>: a syntax error with its line, or the typed values and the validator's messages.</summary>
    SettingsParse Parse(string json);

    /// <summary>Saves changed typed values into the file, keeping everything else in it.</summary>
    Task<SettingsWrite> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default);

    /// <summary>Saves the raw text as the file.</summary>
    Task<SettingsWrite> SaveJsonAsync(string json, CancellationToken cancellationToken = default);
}
