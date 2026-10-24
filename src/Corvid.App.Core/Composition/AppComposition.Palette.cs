using Corvid.App.Core.Keys;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Settings;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private KeyMap? _keys;
    private IPromptHistory? _promptHistory;
    private PaletteViewModel? _palette;

    /// <summary>The keyboard map (docs/plan/ui.md §6): the shell binds the window's keys from it, Settings remaps them.</summary>
    public KeyMap Keys => _keys ??= new KeyMap();

    /// <summary>The workspace's prompts, newest first; the demo's on the frozen clock.</summary>
    public IPromptHistory PromptHistory => _promptHistory ??= new DemoPromptHistory(Time);

    /// <summary>
    /// The command palette and the surfaces that share it (Ctrl+K, Ctrl+Tab, Ctrl+R, Ctrl+F, Ctrl+Shift+F, Alt+P, F1). The
    /// session, the model service and Settings are reached on use, so opening the palette creates none of them early.
    /// </summary>
    public PaletteViewModel Palette => _palette ??= new PaletteViewModel(new PaletteHost(Shell, Keys, Time, Dispatcher)
    {
        CurrentSession = () => Sessions.Session,
        History = PromptHistory,
        Sessions = SessionService,
        Models = () => ModelService,
        SettingsSections = SettingsCatalogue.Sections,
        OpenSettings = section =>
        {
            Settings.SelectSection(section);
            Shell.Navigate(Area.Settings);
        },
        RevealBlock = block => Sessions.Reveal(block),
    });
}
