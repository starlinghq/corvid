using Corvid.App.Core.Settings;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private SettingsViewModel? _settings;

    /// <summary>The Settings area: configuration and the theme.</summary>
    public SettingsViewModel Settings => _settings ??= new SettingsViewModel(Shell);
}
