using System.Collections.ObjectModel;
using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Shell;

/// <summary>
/// The window around every page (docs/plan/ui.md §3): the rail's brand, attention bell, destinations and session groups;
/// which area and session are current; the theme; and which of the rail, the dock and the status bar show at the
/// window's width, with the toggles that bring a hidden one back. The head draws it and reports the width and whether
/// the current page has dock panels; nothing here knows about Uno. Live data sets the same members the demo values set.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly Dictionary<Area, NavItem> _rail;
    private readonly Dictionary<string, int> _attentionSources = new(StringComparer.Ordinal);
    private readonly PhoneTab _chatTab;
    private readonly PhoneTab _changesTab;
    private readonly PhoneTab _attentionTab;
    private readonly PhoneTab _moreTab;
    private Area _area = Area.Sessions;
    private SessionListItem? _session;
    private AppTheme _theme;
    private string _workspace = "";
    private int _attention;
    private ShellLayout _layout = ShellLayout.Wide;
    private bool _hasDock;
    private bool _dockOpen = true;
    private bool _railOpen = true;
    private bool _dockOverlayOpen;
    private bool _railOverlayOpen;
    private bool _pageDrawsRail;
    private bool _setupShowing;

    public ShellViewModel(StatusBarViewModel statusBar)
    {
        ArgumentNullException.ThrowIfNull(statusBar);
        StatusBar = statusBar;
        NavItems = [.. Areas.Rail.Select(area => new NavItem(area, new RelayCommand(() => Navigate(area))))];
        _rail = NavItems.ToDictionary(item => item.Area);
        _chatTab = new PhoneTab("Chat", IconKind.Chat, new RelayCommand(() => Navigate(Area.Sessions)));
        _changesTab = new PhoneTab("Changes", IconKind.Sliders, new RelayCommand(() => Navigate(Area.Review)));
        _attentionTab = new PhoneTab("Attention", IconKind.Bell, new RelayCommand(OpenAttention));
        _moreTab = new PhoneTab("More", IconKind.More, new RelayCommand(() => SetOverlays(rail: true, dock: false)));
        PhoneTabs = [_chatTab, _changesTab, _attentionTab, _moreTab];

        NavigateCommand = new RelayCommand<Area>(Navigate);
        OpenSettingsCommand = new RelayCommand(() => Navigate(Area.Settings));
        OpenDoctorCommand = new RelayCommand(() => Navigate(Area.FirstRun));
        OpenAttentionCommand = new RelayCommand(OpenAttention);
        SetThemeCommand = new RelayCommand<AppTheme>(theme => Theme = theme);
        ToggleDockCommand = new RelayCommand(ToggleDock);
        ToggleRailCommand = new RelayCommand(ToggleRail);
        CloseOverlaysCommand = new RelayCommand(() => SetOverlays(rail: false, dock: false));
        // Both live as long as the window: the phone's top bar shows the status bar's first tag.
        StatusBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatusBarViewModel.Tags) or null or "")
                OnPropertyChanged(nameof(PhoneTags));
        };
        UpdateSelection();
    }

    public StatusBarViewModel StatusBar { get; }

    /// <summary>The rail's destinations, in order (<see cref="Areas.Rail"/>).</summary>
    public IReadOnlyList<NavItem> NavItems { get; }

    /// <summary>The rail's session groups (Pinned, Recent); replaced whole when the list changes.</summary>
    public ObservableCollection<SessionGroup> SessionGroups { get; } = [];

    /// <summary>The phone layout's bottom bar: Chat, Changes, Attention, More.</summary>
    public IReadOnlyList<PhoneTab> PhoneTabs { get; }

    /// <summary>The brand block's second line: the workspace and its branch ("corvid · main").</summary>
    public string Workspace
    {
        get => _workspace;
        set
        {
            if (SetProperty(ref _workspace, value ?? ""))
                OnPropertyChanged(nameof(PhoneSubtitle));
        }
    }

    /// <summary>
    /// The phone top bar's second line: the workspace, except while the page draws its own steps (the first run's setup),
    /// which belongs to no workspace yet.
    /// </summary>
    public string PhoneSubtitle => _setupShowing ? "" : _workspace;

    /// <summary>
    /// The phone top bar's tag: one, since a phone has room for one beside the title and the bell already counts
    /// approvals. Taint always shows (ui.md §7), so "tainted" is the one whenever the bar has it, whatever comes before it
    /// ("read-only", "sessions unavailable"); otherwise the bar's first. The status bar keeps them all at desktop widths.
    /// </summary>
    public IReadOnlyList<StatusTag> PhoneTags => StatusBar.Tags.Count <= 1
        ? StatusBar.Tags
        : [StatusBar.Tags.FirstOrDefault(IsTaint) ?? StatusBar.Tags[0]];

    /// <summary>The taint tag ("tainted"), which the status bar and the phone keep in view whatever else gives way.</summary>
    public static bool IsTaint(StatusTag? tag) => tag is not null && string.Equals(tag.Text, "tainted", StringComparison.Ordinal);

    /// <summary>
    /// Whether the phone top bar's title is the page's level-1 heading. It is wherever the page drops its own title on the
    /// phone (Sessions and the A2 pages); Review, Rewind, Models, Audit and the first run keep their own, so there the top
    /// bar's title is not a heading, and a screen reader finds one level-1 heading per page.
    /// </summary>
    public bool PhoneTitleIsHeading => _area is not (Area.Review or Area.Rewind or Area.Models or Area.Audit or Area.FirstRun);

    /// <summary>
    /// How many items wait in the attention tray, across sessions (the bell's count): the sum of what each source reports
    /// with <see cref="SetAttention"/>. Setting it replaces the count of the unnamed source.
    /// </summary>
    public int AttentionCount
    {
        get => _attention;
        set => SetAttention("", value);
    }

    /// <summary>
    /// Sets how many attention items one source has: the sessions (approval queues and questions), and as their areas
    /// are built, memory reviews, held MCP tools, pairing codes, finished jobs.
    /// </summary>
    public void SetAttention(string source, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        _attentionSources[source] = Math.Max(0, count);
        if (!SetProperty(ref _attention, _attentionSources.Values.Sum(), nameof(AttentionCount)))
            return;
        _attentionTab.Count = _attention;
        OnPropertyChanged(nameof(AttentionText));
        OnPropertyChanged(nameof(AttentionName));
    }

    /// <summary>The bell's count as drawn; empty when nothing waits.</summary>
    public string AttentionText => _attention > 0 ? _attention.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The bell's accessible name, as the mockup labels it.</summary>
    public string AttentionName => _attention switch
    {
        0 => "Attention: nothing waiting",
        1 => "Attention: 1 item waiting",
        _ => $"Attention: {AttentionText} items waiting",
    };

    public Area CurrentArea => _area;

    /// <summary>The current area's title, for the window and the phone's top bar.</summary>
    public string CurrentTitle => _area.Title();

    /// <summary>The selected session, which Sessions shows and Rewind acts on; null when none is open.</summary>
    public SessionListItem? CurrentSession => _session;

    /// <summary>The phone's top bar: the session's title while a session shows, otherwise the area's.</summary>
    public string PhoneTitle => _area is Area.Sessions or Area.Rewind && _session is { } session ? session.Title : CurrentTitle;

    public AppTheme Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    /// <summary>The arrangement for the window's width; the view sets it from <see cref="ShellLayouts.ForWidth"/>.</summary>
    public ShellLayout Layout
    {
        get => _layout;
        set
        {
            if (!SetProperty(ref _layout, value))
                return;
            // An overlay belongs to the width it was opened at.
            _railOverlayOpen = false;
            _dockOverlayOpen = false;
            RaiseLayout();
        }
    }

    /// <summary>Whether the current page puts panels in the dock (a session's Changes and Context, the Board's tray).</summary>
    public bool HasDock
    {
        get => _hasDock;
        set
        {
            if (SetProperty(ref _hasDock, value))
                RaiseLayout();
        }
    }

    /// <summary>
    /// The current page draws its own left column instead of the rail (the first run's setup steps, FirstRun.dc.html), so
    /// the rail's column and its status-bar toggle stay away while it shows. The page's view model sets it while its area
    /// is current and clears it when the area goes.
    /// </summary>
    public bool PageDrawsRail
    {
        get => _pageDrawsRail;
        set
        {
            if (SetProperty(ref _pageDrawsRail, value))
                RaiseLayout();
        }
    }

    /// <summary>The rail as a column (wide and medium widths, unless collapsed with Ctrl+Shift+B or the page draws its own).</summary>
    public bool ShowRail => _layout is ShellLayout.Wide or ShellLayout.Medium && _railOpen && !_pageDrawsRail;

    /// <summary>The rail over the content (narrow and phone widths, after its toggle, the phone's menu or More).</summary>
    public bool ShowRailOverlay => _layout is ShellLayout.Narrow or ShellLayout.Phone && _railOverlayOpen;

    /// <summary>The dock as a column (wide widths, unless closed with Ctrl+B).</summary>
    public bool ShowDock => _hasDock && _layout == ShellLayout.Wide && _dockOpen;

    /// <summary>The dock over the content (medium and narrow widths, after its toggle).</summary>
    public bool ShowDockOverlay => _hasDock && _layout is ShellLayout.Medium or ShellLayout.Narrow && _dockOverlayOpen;

    /// <summary>The scrim behind an overlay, which closes it when clicked.</summary>
    public bool ShowScrim => ShowRailOverlay || ShowDockOverlay;

    public bool ShowStatusBar => _layout != ShellLayout.Phone;

    /// <summary>
    /// The status bar's approval-mode segments, at 860 px and wider. The narrow mockups drop them so the safety tags
    /// ("tainted", "2 approvals") keep their place; Shift+Tab still cycles the mode.
    /// </summary>
    public bool ShowModeControl => _layout is ShellLayout.Wide or ShellLayout.Medium;

    /// <summary>The phone layout's top bar.</summary>
    public bool ShowPhoneBars => _layout == ShellLayout.Phone;

    /// <summary>
    /// The phone's bottom bar (Chat, Changes, Attention, More) and the top bar's menu and bell: not while the page draws
    /// its own steps, since the first run's setup is a path of its own with its own way out (Skip setup).
    /// </summary>
    public bool ShowPhoneTabs => _layout == ShellLayout.Phone && !_setupShowing;

    /// <summary>
    /// The first run's setup shows (not its checks reached from Doctor): at every width a path of its own, so the phone
    /// drops the session's bottom bar, menu, bell and workspace line. The first run's view model sets it while its setup is
    /// the current area and clears it when it is not.
    /// </summary>
    public bool SetupShowing
    {
        get => _setupShowing;
        set
        {
            if (!SetProperty(ref _setupShowing, value))
                return;
            RaiseLayout();
            OnPropertyChanged(nameof(PhoneSubtitle));
        }
    }

    /// <summary>The status bar's rail toggle, shown whenever the rail is hidden on a desktop width.</summary>
    public bool ShowRailToggle => _layout != ShellLayout.Phone && !ShowRail && !_pageDrawsRail;

    /// <summary>The status bar's dock toggle, shown whenever the page has panels the width or Ctrl+B hid.</summary>
    public bool ShowDockToggle => _hasDock && _layout != ShellLayout.Phone && !ShowDock;

    public RelayCommand<Area> NavigateCommand { get; }

    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>Settings is the current page: its link at the rail's foot is marked as a rail row is.</summary>
    public bool IsSettingsCurrent => _area == Area.Settings;

    /// <summary>The rail footer's Doctor: the first run's checks, which are <c>corvid doctor</c>'s.</summary>
    public RelayCommand OpenDoctorCommand { get; }

    /// <summary>The bell: the attention tray, which lives beside the Board.</summary>
    public RelayCommand OpenAttentionCommand { get; }

    public RelayCommand<AppTheme> SetThemeCommand { get; }

    /// <summary>Ctrl+B: the dock column at wide widths, the dock overlay below them.</summary>
    public RelayCommand ToggleDockCommand { get; }

    /// <summary>Ctrl+Shift+B: the rail column at wide and medium widths, the rail overlay below them.</summary>
    public RelayCommand ToggleRailCommand { get; }

    /// <summary>Esc or a click on the scrim.</summary>
    public RelayCommand CloseOverlaysCommand { get; }

    /// <summary>Shows an area. Opening one closes any overlay, as following a link in a drawer does.</summary>
    public void Navigate(Area area)
    {
        SetOverlays(rail: false, dock: false);
        if (_area == area)
            return;
        _area = area;
        OnPropertyChanged(nameof(CurrentArea));
        OnPropertyChanged(nameof(PhoneTitleIsHeading));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(PhoneTitle));
        UpdateSelection();
    }

    /// <summary>The link last followed into an area (<see cref="Follow"/>); the area it names reads it to land on its target.</summary>
    public AreaLink? Link { get; private set; }

    /// <summary>
    /// Follows a link into an area: publishes it as <see cref="Link"/> (raised even when the link is the same or the area
    /// already shows, so following it again lands again), then shows the area.
    /// </summary>
    public void Follow(AreaLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        Link = link;
        OnPropertyChanged(nameof(Link));
        Navigate(link.Area);
    }

    /// <summary>Selects a session by id and shows it; false (and no change) when the rail lists no such session.</summary>
    public bool OpenSession(string id)
    {
        if (FindSession(id) is not { } session)
            return false;
        SelectSession(session);
        Navigate(Area.Sessions);
        return true;
    }

    public SessionListItem? FindSession(string id) =>
        SessionGroups.SelectMany(group => group.Sessions).FirstOrDefault(session => string.Equals(session.Id, id, StringComparison.Ordinal));

    /// <summary>Sets an area's rail badge; Review's is also the phone's Changes count.</summary>
    public void SetBadge(Area area, int count)
    {
        if (!_rail.TryGetValue(area, out var item))
            throw new ArgumentOutOfRangeException(nameof(area), area, "Only the rail's areas carry a badge.");
        item.Badge = count;
        if (area == Area.Review)
            _changesTab.Count = item.Badge;
    }

    public NavItem RailItem(Area area) => _rail[area];

    /// <summary>
    /// Replaces the rail's session groups. The selection follows the session's id, so a refreshed list keeps the open
    /// session selected; when that session is gone, nothing is.
    /// </summary>
    public void SetSessions(IEnumerable<SessionGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var current = _session?.Id;
        SessionGroups.Clear();
        foreach (var group in groups)
            SessionGroups.Add(group);
        SelectSession(current is null ? null : FindSession(current));
    }

    /// <summary>Builds a rail entry whose click opens it in this shell.</summary>
    public SessionListItem CreateSession(string id, string title, SessionState state, string when)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new SessionListItem(id, title, state, when, new RelayCommand(() => OpenSession(id)));
    }

    /// <summary>Selects a session without changing the area (the command line's <c>--session</c> with <c>--page</c>).</summary>
    public void SelectSession(SessionListItem? session)
    {
        if (ReferenceEquals(_session, session))
            return;
        if (_session is not null)
            _session.IsSelected = false;
        _session = session;
        if (_session is not null)
            _session.IsSelected = SessionRowShows;
        // The status bar names the session the window shows.
        StatusBar.SessionId = _session?.Id ?? "";
        OnPropertyChanged(nameof(CurrentSession));
        OnPropertyChanged(nameof(PhoneTitle));
    }

    private void OpenAttention() => Navigate(Area.Board);

    private void ToggleDock()
    {
        switch (_layout)
        {
            case ShellLayout.Wide:
                _dockOpen = !_dockOpen;
                RaiseLayout();
                break;
            case ShellLayout.Medium or ShellLayout.Narrow when _hasDock:
                SetOverlays(rail: false, dock: !_dockOverlayOpen);
                break;
        }
    }

    private void ToggleRail()
    {
        if (_layout is ShellLayout.Wide or ShellLayout.Medium)
        {
            _railOpen = !_railOpen;
            RaiseLayout();
        }
        else
        {
            SetOverlays(rail: !_railOverlayOpen, dock: false);
        }
    }

    private void SetOverlays(bool rail, bool dock)
    {
        if (_railOverlayOpen == rail && _dockOverlayOpen == dock)
            return;
        _railOverlayOpen = rail;
        _dockOverlayOpen = dock;
        RaiseLayout();
    }

    private void UpdateSelection()
    {
        var row = _area.RailRow();
        foreach (var item in NavItems)
            item.IsSelected = item.Area == row;
        _chatTab.IsSelected = _area is Area.Sessions or Area.Rewind;
        _changesTab.IsSelected = _area == Area.Review;
        _attentionTab.IsSelected = _area == Area.Board;
        _moreTab.IsSelected = !(_chatTab.IsSelected || _changesTab.IsSelected || _attentionTab.IsSelected);
        OnPropertyChanged(nameof(IsSettingsCurrent));
        if (_session is not null)
            _session.IsSelected = SessionRowShows;
    }

    /// <summary>
    /// The open session's rail row is marked only while a page of that session shows (Sessions, Review, Rewind): on
    /// another area the area's own row is the current one, as Models.png and Audit.png draw the rail.
    /// </summary>
    private bool SessionRowShows => _area is Area.Sessions or Area.Review or Area.Rewind;

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(ShowRail));
        OnPropertyChanged(nameof(ShowRailOverlay));
        OnPropertyChanged(nameof(ShowDock));
        OnPropertyChanged(nameof(ShowDockOverlay));
        OnPropertyChanged(nameof(ShowScrim));
        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(ShowModeControl));
        OnPropertyChanged(nameof(ShowPhoneBars));
        OnPropertyChanged(nameof(ShowPhoneTabs));
        OnPropertyChanged(nameof(ShowRailToggle));
        OnPropertyChanged(nameof(ShowDockToggle));
    }
}
