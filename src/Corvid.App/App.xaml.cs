using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Infrastructure;
using Corvid.App.Shell;

namespace Corvid.App;

/// <summary>
/// The application: one window titled "Corvid" holding the shell, built from the options the command line gave. Inter is
/// made the default text font before any text is laid out, so every page draws the mockups' type without naming it.
/// </summary>
public partial class App : Application
{
    private readonly AppOptions _options;
    private Window? _window;

    public App(AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
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
        var composition = new AppComposition(_options, new UiDispatcher(_window.DispatcherQueue));
        _window.Content = new ShellPage(composition);
        _window.Activate();
    }
}
