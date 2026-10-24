using Corvid.App.Core.Settings;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SettingsViewModel? _settings;
    private ISettingsService? _settingsService;

    /// <summary>
    /// The configuration as the views see it. Demo mode (the engine's comes from <see cref="Services"/> instead) edits an in-memory
    /// corvid.json and writes nothing.
    /// </summary>
    public ISettingsService SettingsService => _settingsService ??= Services.CreateSettingsService(this);

    /// <summary>The Settings area: corvid.json typed and raw, keybindings, themes, notifications and layout.</summary>
    public SettingsViewModel Settings => _settings ??= new SettingsViewModel(Shell, SettingsService, Keys);
}
