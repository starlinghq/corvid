namespace Corvid.App.Core.Settings;

/// <summary>How a setting is edited.</summary>
public enum SettingKind
{
    /// <summary>A line of text (a name, a Vault secret's name).</summary>
    Text,

    /// <summary>A whole number or a fraction, checked against its range.</summary>
    Number,

    /// <summary>One of a few words (Manual, Smart, Permissive).</summary>
    Choice,

    /// <summary>On or off.</summary>
    Toggle,

    /// <summary>An absolute path, or one starting with <c>~/</c>.</summary>
    Path,

    /// <summary>An http or https address.</summary>
    Url,
}

/// <summary>When a change takes effect, as the page tags it (docs/plan/ui.md F24: "what needs a restart").</summary>
public enum SettingApplies
{
    /// <summary>At once: the running sessions read it on their next use.</summary>
    Now,

    /// <summary>From the next turn on; a turn already running keeps what it started with.</summary>
    NextTurn,

    /// <summary>Read when the host starts (models, devices, the listener, paths): after a restart.</summary>
    Restart,
}

/// <summary>Where a setting lives.</summary>
public enum SettingStore
{
    /// <summary><c>corvid.json</c>, under <c>Corvid</c>; checked by the host's validator.</summary>
    CorvidJson,

    /// <summary>The app's own preferences (notifications, layout, density), which <c>ui.db</c> will keep (G-7).</summary>
    App,
}

/// <summary>
/// One typed setting: its key under <c>Corvid</c> ("Approvals:TimeoutSeconds"), the section and label the page shows it
/// under, a sentence on what it does, how it is edited, when it applies, and its default.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    string Section,
    string Label,
    string Description,
    SettingKind Kind,
    SettingApplies Applies,
    string Default)
{
    public SettingStore Store { get; init; } = SettingStore.CorvidJson;

    /// <summary>The words a choice takes, in order.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>The full key as the validator names it: "Corvid:Approvals:TimeoutSeconds".</summary>
    public string FullKey => Store == SettingStore.CorvidJson ? $"Corvid:{Key}" : $"App:{Key}";
}

/// <summary>A validator's message, for one setting (by <see cref="SettingDefinition.Key"/>) or, with no key, the whole file.</summary>
public sealed record SettingProblem(string? Key, string Message);

/// <summary>
/// The configuration as loaded: where it lives, its text, each typed setting's value as text, the values given on the
/// command line with <c>--set</c> for this run only, and whether it is the demo's in-memory copy.
/// </summary>
public sealed record SettingsSnapshot(
    string Path,
    string Json,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> RunOverrides,
    bool IsDemo);

/// <summary>A raw <c>corvid.json</c> read: the typed values found in it, and its problems (a syntax error, or the validator's).</summary>
public sealed record SettingsParse(IReadOnlyDictionary<string, string> Values, IReadOnlyList<SettingProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>What a save did: written (or kept in the demo's memory), the sentence to show, its problems, and the keys that wait on a restart.</summary>
public sealed record SettingsWrite(bool Written, string Message, IReadOnlyList<SettingProblem> Problems, IReadOnlyList<string> RestartKeys);
