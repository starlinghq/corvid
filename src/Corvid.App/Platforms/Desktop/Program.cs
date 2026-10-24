using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Engine;
using Corvid.App.Infrastructure;
using Corvid.Host;
using Uno.UI.Hosting;

namespace Corvid.App;

/// <summary>
/// The desktop head: X11 and the framebuffer on Linux, Win32 on Windows, AppKit on macOS. The command line is read
/// first, so a mistyped option prints the usage instead of opening a window. With <c>--engine</c> it is the only head that
/// runs Corvid in-process: it starts the host and checks the model cache before the window opens (quick and offline), then
/// loads the models in the background while the window paints; when models are missing it opens First run instead.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!AppOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(AppOptions.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(AppOptions.Usage);
            return 0;
        }

        AppLogging.Initialize();
        AppEngine? engine = null;
        IAppServices? services = null;
        if (!options.Demo)
        {
            // Before the UI thread exists, so nothing waits on it: the host's start validates corvid.json and the check
            // reads file sizes only. The models load on the pool from here on.
            engine = AppEngine.StartAsync(CorvidHost.CreateBuilder().Build(), ownsHost: true,
                new Progress(step => Console.Error.WriteLine($"corvid app: {step.Description}{(step.Elapsed is { } took ? $" ({took.TotalSeconds:0.0} s)" : "")}")))
                .GetAwaiter().GetResult();
            if (engine.StartFailure is { } failure)
                Console.Error.WriteLine("corvid app: " + failure);
            // Missing models open First run, unless the command line named a page (an area's builder looks at its own page).
            if (!engine.MissingModels.IsEmpty && options.Page is null)
                options = options with { Page = Area.FirstRun };
            services = new EngineAppServices(engine);
        }

        // CORVID_APP_RENDERER=software draws with Skia on the CPU instead of OpenGL. The visual harness sets it: under Xvfb,
        // Uno's OpenGL path sometimes keeps the old height after the window shrinks and draws the frame offset.
        if (string.Equals(Environment.GetEnvironmentVariable("CORVID_APP_RENDERER"), "software", StringComparison.OrdinalIgnoreCase))
            FeatureConfiguration.Rendering.UseOpenGLOnX11 = false;
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(options, services))
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();
        host.Run();
        // The window is gone and the UI thread with it: stop the runtime (its sessions, MCP servers, background indexing).
        engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Progress as it is reported, from the pool: standard error is safe from any thread.</summary>
    private sealed class Progress(Action<Corvid.Host.Composition.StartupStep> report) : IProgress<Corvid.Host.Composition.StartupStep>
    {
        public void Report(Corvid.Host.Composition.StartupStep value) => report(value);
    }
}
