using Corvid.App.Core.Keys;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Settings;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private KeyMap? _keys;
    private IPromptHistory? _promptHistory;
    private PaletteViewModel? _palette;
    private ISessionSearch? _sessionSearch;
    private bool _sessionSearchMade;

    /// <summary>The keyboard map (docs/plan/ui.md §6): the shell binds the window's keys from it, Settings remaps them.</summary>
    public KeyMap Keys => _keys ??= new KeyMap();

    /// <summary>The workspace's prompts, newest first; the demo's on the frozen clock.</summary>
    public IPromptHistory PromptHistory => _promptHistory ??= Services.CreatePromptHistory(this);

    /// <summary>G-5: search over what was said across sessions; null where there is no episodic index (the demo).</summary>
    public ISessionSearch? SessionSearch
    {
        get
        {
            if (!_sessionSearchMade)
            {
                _sessionSearch = Services.CreateSessionSearch(this);
                _sessionSearchMade = true;
            }

            return _sessionSearch;
        }
    }

    /// <summary>
    /// The command palette and the surfaces that share it (Ctrl+K, Ctrl+Tab, Ctrl+R, Ctrl+F, Ctrl+Shift+F, Alt+P, F1). The
    /// session, the model service and Settings are reached on use, so opening the palette creates none of them early.
    /// </summary>
    public PaletteViewModel Palette => _palette ??= new PaletteViewModel(new PaletteHost(Shell, Keys, Time, Dispatcher)
    {
        CurrentSession = () => Sessions.Session,
        History = PromptHistory,
        Search = SessionSearch,
        RevealAt = (session, position) => Sessions.RevealAt(session, position),
        Sessions = SessionService,
        Models = () => ModelService,
        SettingsSections = SettingsCatalogue.Sections,
        OpenSettings = section =>
        {
            Settings.SelectSection(section);
            Shell.Navigate(Area.Settings);
        },
        RevealBlock = block => Sessions.Reveal(block),
        NewSession = SessionHub.CanCreate ? SessionHub.StartNew : null,
    });
}
