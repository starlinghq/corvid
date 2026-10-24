using Microsoft.Extensions.Logging;

namespace Corvid.App.Infrastructure;

/// <summary>
/// Uno's own warnings and errors go to standard error in every build, so a failed binding, a missing resource or a font
/// that did not load shows up in the app's log (the visual harness reads it). Nothing below a warning is logged.
/// </summary>
internal static class AppLogging
{
    public static void Initialize()
    {
        var factory = LoggerFactory.Create(builder => builder
            .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning));
        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
    }
}
