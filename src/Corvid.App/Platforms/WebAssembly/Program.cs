using Corvid.App.Core;
using Corvid.App.Infrastructure;
using Uno.UI.Hosting;

namespace Corvid.App;

/// <summary>
/// The WebAssembly head (built only with -p:CorvidAppWasm=true). A browser tab has no command line, so it opens the demo
/// on Sessions in the system's theme; attached mode (docs/plan/ui.md §2) will give it a real session later.
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        AppLogging.Initialize();
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(new AppOptions { Demo = true }))
            .UseWebAssembly()
            .Build();
        await host.RunAsync();
    }
}
