using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Shell;

/// <summary>The session's approval mode (docs/plan/ui.md §4.4, G-10): the status bar's segmented control.</summary>
public enum ApprovalMode
{
    Manual,
    Smart,
    Permissive,
}

/// <summary>A tag in the status bar or a header ("tainted", "Vault absent"), with the hover text that explains it.</summary>
public sealed record StatusTag(string Text, Tone Tone, string Detail = "");

/// <summary>A labelled item an area adds to the status bar ("downloads kev-4b 42%", "memory 212 facts").</summary>
public sealed record StatusField(string Label, string Value);

/// <summary>
/// The status bar (docs/plan/ui.md §3, §4.5): the generator slot with its device, queue and latency; the decider tier;
/// the context gauge; the approval mode; tags such as taint; the scheduler; the branch; the session; and, at the right,
/// the running turn's elapsed time. Every item is optional, because pages show different sets (the Board shows the
/// engine and the gateway, the first run neither mode nor session); the view hides what is empty and keeps one order.
/// </summary>
public sealed class StatusBarViewModel : ObservableObject
{
    private string _engine = "";
    private string _generator = "";
    private string _decider = "";
    private string _context = "";
    private IReadOnlyList<StatusField> _areaFields = [];
    private ApprovalMode? _mode;
    private IReadOnlyList<StatusTag> _tags = [];
    private string _scheduler = "";
    private string _gateway = "";
    private string _hosting = "";
    private string _branch = "";
    private string _sessionId = "";
    private string _trailing = "";
    private string _sessionTrailing = "";

    public StatusBarViewModel()
    {
        RequestModeCommand = new RelayCommand<ApprovalMode>(RequestMode, _ => _mode is not null);
        CycleModeCommand = new RelayCommand(() => RequestMode(Next(_mode ?? ApprovalMode.Manual)), () => _mode is not null);
        Modes = [.. Enum.GetValues<ApprovalMode>().Select(mode =>
            new Choice(mode.ToString().ToLowerInvariant(), Describe(mode), new RelayCommand(() => RequestMode(mode))))];
    }

    /// <summary>
    /// The person picked a mode. The view model does not change <see cref="Mode"/> itself: whoever hosts the session
    /// (the engine, or the demo) applies the request and reports the mode back, so the bar always shows the mode in force.
    /// </summary>
    public event EventHandler<ApprovalMode>? ModeRequested;

    /// <summary>Where the engine runs ("in-process · 2 workspaces"), on pages that show it instead of the generator.</summary>
    public string Engine { get => _engine; set => SetProperty(ref _engine, value ?? ""); }

    /// <summary>The generator slot: model, device, queue depth and median latency ("claude (escalated) · cuda:0 · q0 · 410 ms").</summary>
    public string Generator { get => _generator; set => SetProperty(ref _generator, value ?? ""); }

    /// <summary>The decider tier ("Kev-0.8B, calibrated").</summary>
    public string Decider { get => _decider; set => SetProperty(ref _decider, value ?? ""); }

    /// <summary>The context gauge ("38%").</summary>
    public string Context { get => _context; set => SetProperty(ref _context, value ?? ""); }

    /// <summary>Items the current area adds after the context gauge (downloads on Models, memory on Memory).</summary>
    public IReadOnlyList<StatusField> AreaFields { get => _areaFields; set => SetProperty(ref _areaFields, value ?? []); }

    /// <summary>The session's approval mode; null hides the control (no session yet, as on the first run).</summary>
    public ApprovalMode? Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value))
                return;
            OnPropertyChanged(nameof(HasMode));
            foreach (var (choice, mode) in Modes.Zip(Enum.GetValues<ApprovalMode>()))
                choice.IsSelected = mode == value;
            RequestModeCommand.NotifyCanExecuteChanged();
            CycleModeCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasMode => _mode is not null;

    /// <summary>The segmented control's three segments, in the order Manual, Smart, Permissive; the one in force is selected.</summary>
    public IReadOnlyList<Choice> Modes { get; }

    /// <summary>Tags after the mode control: "tainted" while outside content is in context, "Vault absent" on the first run.</summary>
    public IReadOnlyList<StatusTag> Tags { get => _tags; set => SetProperty(ref _tags, value ?? []); }

    /// <summary>The scheduler's state ("here · 2 jobs").</summary>
    public string Scheduler { get => _scheduler; set => SetProperty(ref _scheduler, value ?? ""); }

    /// <summary>The gateway's adapters ("telegram up · discord up · slack off"), on pages that show it.</summary>
    public string Gateway { get => _gateway; set => SetProperty(ref _gateway, value ?? ""); }

    /// <summary>
    /// What this app hosts for the machine (U-8), on every page while it hosts any: "gateway", "scheduler" or both; empty
    /// when it hosts neither.
    /// </summary>
    public string Hosting { get => _hosting; set => SetProperty(ref _hosting, value ?? ""); }

    /// <summary>The workspace's git branch.</summary>
    public string Branch { get => _branch; set => SetProperty(ref _branch, value ?? ""); }

    /// <summary>The current session's id.</summary>
    public string SessionId { get => _sessionId; set => SetProperty(ref _sessionId, value ?? ""); }

    /// <summary>
    /// The current area's right-hand item: the running turn's elapsed time ("turn 48 s") on a session's pages, the Board's
    /// activity, Audit's log size; or "idle", or nothing. What the view draws is <see cref="ShownTrailing"/>.
    /// </summary>
    public string Trailing
    {
        get => _trailing;
        set
        {
            if (SetProperty(ref _trailing, value ?? ""))
                OnPropertyChanged(nameof(ShownTrailing));
        }
    }

    /// <summary>
    /// The open session's right-hand item whatever the area ("turn 48 s", "idle"); the sessions keep it current. While
    /// <see cref="Trailing"/> still holds the session's last value (a page that writes no item of its own, after a
    /// session's page), it follows, so it never stands frozen at an old turn time.
    /// </summary>
    public string SessionTrailing
    {
        get => _sessionTrailing;
        set
        {
            var previous = _sessionTrailing;
            if (!SetProperty(ref _sessionTrailing, value ?? ""))
                return;
            if (previous.Length > 0 && string.Equals(_trailing, previous, StringComparison.Ordinal))
                Trailing = _sessionTrailing;
            OnPropertyChanged(nameof(ShownTrailing));
        }
    }

    /// <summary>
    /// The right-hand item as drawn: the area's own when it has one, otherwise the open session's. An area's "idle" says
    /// only that it knows of no turn, so a running turn's time shows over it: every page says the same while a turn runs,
    /// and an area that writes nothing (Settings, Secrets) shows the session's rather than the last area's item (areas
    /// give theirs back when they go: the status bar lease, Audit).
    /// </summary>
    public string ShownTrailing => _trailing.Length > 0 && !(_trailing == "idle" && _sessionTrailing.Length > 0) ? _trailing : _sessionTrailing;

    /// <summary>Asks for a mode (the segmented control's buttons).</summary>
    public RelayCommand<ApprovalMode> RequestModeCommand { get; }

    /// <summary>Asks for the next mode in the cycle Manual, Smart, Permissive (the TUI's Shift+Tab).</summary>
    public RelayCommand CycleModeCommand { get; }

    /// <summary>One sentence per mode, as the first run and the control's hover text say it (docs/plan/ui.md §4.4).</summary>
    public static string Describe(ApprovalMode mode) => mode switch
    {
        ApprovalMode.Manual => "Manual: every side effect asks.",
        ApprovalMode.Smart => "Smart: runs unasked only when Kev rates it low risk and it stays in the workspace, with no network, elevation or destructive tool.",
        _ => "Permissive: follows the fabric; calibrated levels 0 to 1 run, 2 to 4 ask.",
    };

    private void RequestMode(ApprovalMode mode)
    {
        if (_mode is not null)
            ModeRequested?.Invoke(this, mode);
    }

    private static ApprovalMode Next(ApprovalMode mode) => mode switch
    {
        ApprovalMode.Manual => ApprovalMode.Smart,
        ApprovalMode.Smart => ApprovalMode.Permissive,
        _ => ApprovalMode.Manual,
    };
}
