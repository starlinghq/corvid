using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

/// <summary>
/// Builds the window's view models and the services behind them. Engine mode (the default) takes each area's service
/// from the <see cref="IAppServices"/> the head gives it (Corvid.App.Engine's, in the desktop head), on the system clock.
/// Demo mode (<c>--demo</c>) fills the shell with the Main mockup's values, backs each area with its demo service
/// (<see cref="DemoAppServices"/>) and freezes the clock at 2026-10-24 14:05 local, so screenshots are stable.
/// <para>
/// The class is partial with one file per area (<c>AppComposition.&lt;Area&gt;.cs</c>), each holding a property named
/// after the area that creates the area's service (through <see cref="Services"/>) and view model on first use. Agents
/// building different areas never edit the same file, and a page reaches its view model through the composition it is
/// navigated with.
/// </para>
/// </summary>
public sealed partial class AppComposition : IDisposable
{
    /// <summary>Engine mode's refusal when the head gave the composition no engine.</summary>
    public const string NoEngine = "This window has no engine: the engine needs the desktop app, which runs Corvid in-process. --demo shows the demo.";

    /// <param name="time">
    /// The clock; by default frozen at the demo's minute in demo mode and the system clock otherwise. Tests pass a manual
    /// clock so no timer fires on another thread while they assert.
    /// </param>
    /// <param name="services">
    /// Where each area's service comes from. By default the demo's in demo mode; in engine mode with none given, every area
    /// refuses with <see cref="NoEngine"/> (a head that cannot host the engine).
    /// </param>
    public AppComposition(AppOptions options, IDispatcher dispatcher, TimeProvider? time = null, IAppServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        Options = options;
        Dispatcher = dispatcher;
        Time = time ?? (options.Demo ? FrozenTimeProvider.Demo() : TimeProvider.System);
        Services = services ?? (options.Demo ? new DemoAppServices() : new UnavailableAppServices(NoEngine));

        StatusBar = new StatusBarViewModel();
        Shell = new ShellViewModel(StatusBar) { Theme = options.Theme };
        if (options.Demo)
        {
            DemoShell.Fill(StatusBar);
            DemoShell.Fill(Shell);
        }

        // The session service feeds the rail, the attention count and the status bar from here on, and selects the
        // session the command line named.
        StartSessions();
        // The Board's service counts the other areas' attention items for the bell from the start, so the bell reads the
        // same whichever page opens first, not DemoShell's placeholder until MCP, Gateway or the Board is visited.
        _ = BoardService;
        _ = SkillsService; // counts the waiting drafts for the rail's Skills badge before the page is visited
        Shell.Navigate(options.Page ?? Area.Sessions);
        // G-7: the layout the person chose, from ui.db.
        if (!options.Demo && Services.GetType() != typeof(UnavailableAppServices))
            PreferencesApplied = ApplyPreferencesAsync();
    }

    public AppOptions Options { get; }

    /// <summary>Where each area's service comes from: the demo's or the engine's.</summary>
    public IAppServices Services { get; }

    /// <summary>The UI thread, for services that report from other threads.</summary>
    public IDispatcher Dispatcher { get; }

    /// <summary>The clock every view model reads; frozen in demo mode.</summary>
    public TimeProvider Time { get; }

    public ShellViewModel Shell { get; }

    public StatusBarViewModel StatusBar { get; }

    /// <summary>
    /// Stops the session feeds, the service behind them and every timer they started: the Board's tick and session
    /// watches, and the model store's downloads; then every other service it made that holds something, and the
    /// <see cref="Services"/> they came from (the engine's subscriptions to the runtime's feed and events).
    /// </summary>
    public void Dispose()
    {
        DisposeBoard();
        DisposeSafety();
        DisposeSecrets();
        _skills?.Dispose();
        (_modelService as IDisposable)?.Dispose();
        // A2 ops: the Jobs, MCP and Gateway view models and the demo services that follow the Board's.
        _jobs?.Dispose();
        _mcp?.Dispose();
        _gateway?.Dispose();
        (_mcpService as IDisposable)?.Dispose();
        (_gatewayService as IDisposable)?.Dispose();
        SessionHub.Dispose();
        (SessionService as IDisposable)?.Dispose();
        object?[] others = [_boardService, _reviewService, _rewindService, _auditService, _memoryService, _skillsService, _jobsService,
            _firstRunService, _settingsService, _promptHistory];
        foreach (var service in others)
            (service as IDisposable)?.Dispose();
        (Services as IDisposable)?.Dispose();
    }
}
