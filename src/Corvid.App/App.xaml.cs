using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Infrastructure;
using Corvid.App.Shell;

namespace Corvid.App;

/// <summary>
/// The application: one window titled "Corvid" holding the shell, built from the options the command line gave and the
/// services the head chose (the in-process engine's from the desktop head by default; the demo's with <c>--demo</c> and in the
/// browser head).
/// Inter is made the default text font before any text is laid out, so every page draws the mockups' type without naming it.
/// </summary>
public partial class App : Application
{
    private readonly AppOptions _options;
    private readonly IAppServices? _services;
    private readonly Core.Mvvm.LateDispatcher? _attached;
    private Window? _window;
    private AppComposition? _composition;

    /// <param name="services">Each area's service source; null for the demo's (or, in engine mode, a refusal: no engine here).</param>
    /// <param name="attached">G-1: the dispatcher an attached connection already posts to, bound to the window's once it exists.</param>
    public App(AppOptions options, IAppServices? services = null, Core.Mvvm.LateDispatcher? attached = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _services = services;
        _attached = attached;
        FeatureConfiguration.Font.DefaultTextFontFamily = "ms-appx:///Assets/Fonts/Inter";
        // A theme named on the command line is the application's too, not only the root page's: Uno resolves the theme
        // resources of a control's focus and pointer states against the application's theme, so under --theme light on
        // a system in dark a keyboard-focused card or row redrew in dark brushes. Uno honours this only before start-up.
        if (options.Theme != AppTheme.System)
            RequestedTheme = options.Theme == AppTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) => Console.Error.WriteLine($"Unhandled exception: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "Corvid" };
        var dispatcher = new UiDispatcher(_window.DispatcherQueue);
        _attached?.Bind(dispatcher);
        _composition = new AppComposition(_options, dispatcher, services: _services);
        // The composition's feeds, timers and the engine's subscriptions end with the window; the head stops the runtime.
        _window.Closed += (_, _) =>
        {
            _composition?.Dispose();
            _composition = null;
        };
        _window.Content = new ShellPage(_composition);
        _window.Activate();
    }
}
