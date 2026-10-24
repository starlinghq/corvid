using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

/// <summary>
/// Builds the window's view models and the services behind them. Demo mode (the default until Corvid.App.Engine exists)
/// fills the shell with the Main mockup's values, backs each area with its demo service as the area is built, and freezes
/// the clock at 2026-10-24 14:05 local, so screenshots are stable.
/// <para>
/// The class is partial with one file per area (<c>AppComposition.&lt;Area&gt;.cs</c>), each holding a property named
/// after the area that creates the area's service and view model on first use. Agents building different areas never
/// edit the same file, and a page reaches its view model through the composition it is navigated with.
/// </para>
/// </summary>
public sealed partial class AppComposition : IDisposable
{
    /// <param name="time">
    /// The clock; by default frozen at the demo's minute in demo mode and the system clock otherwise. Tests pass a manual
    /// clock so no timer fires on another thread while they assert.
    /// </param>
    public AppComposition(AppOptions options, IDispatcher dispatcher, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        Options = options;
        Dispatcher = dispatcher;
        Time = time ?? (options.Demo ? FrozenTimeProvider.Demo() : TimeProvider.System);

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
    }

    public AppOptions Options { get; }

    /// <summary>The UI thread, for services that report from other threads.</summary>
    public IDispatcher Dispatcher { get; }

    /// <summary>The clock every view model reads; frozen in demo mode.</summary>
    public TimeProvider Time { get; }

    public ShellViewModel Shell { get; }

    public StatusBarViewModel StatusBar { get; }

    /// <summary>
    /// Stops the session feeds, the service behind them and every timer they started: the Board's tick and session
    /// watches, and the model store's downloads.
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
    }
}
