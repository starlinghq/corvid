using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Engine;
using Corvid.App.Infrastructure;
using Corvid.Host;
using Uno.UI.Hosting;

namespace Corvid.App;

/// <summary>
/// The desktop head: X11 and the framebuffer on Linux, Win32 on Windows, AppKit on macOS. The command line is read
/// first, so a mistyped option prints the usage instead of opening a window. It is the only head that runs Corvid in-process,
/// which it does unless <c>--demo</c> asks for the demo: it starts the host and checks the model cache before the window opens (quick and offline), then
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
        LateDispatcher? attachedDispatcher = null;
        RemoteSessionService? attached = null;
        if (options.Attach is { } address)
        {
            // G-1: another Corvid's sessions in this window. Connected before the window opens (its updates wait for the
            // window's dispatcher); a refusal opens the window anyway, every area saying why.
            attachedDispatcher = new LateDispatcher();
            // A6 review M3: a host that accepts and then says nothing opens the window refused after a while, never hung.
            using var connecting = new CancellationTokenSource(AttachedStartup.ConnectTimeout);
            var result = AttachedStartup.ConnectAsync(address, options.Pair, $"{Environment.MachineName} (Corvid app)", attachedDispatcher,
                new FileDeviceTokenStore(), AttachedPaths.OwnerTokenFile, connecting.Token).GetAwaiter().GetResult();
            Console.Error.WriteLine("corvid app: " + result.Report);
            services = result.Services;
            attached = result.Connection;
            if (attached is not null)
                attached.Disconnected += (_, reason) => Console.Error.WriteLine("corvid app: the attached Corvid disconnected: " + reason);
        }
        else if (!options.Demo)
        {
            // Before the UI thread exists, so nothing waits on it: the host's start validates corvid.json and the check
            // reads file sizes only. The models load on the pool from here on.
            try
            {
                engine = AppEngine.StartAsync(CorvidHost.CreateBuilder().Build(), ownsHost: true,
                    new Progress(step => Console.Error.WriteLine($"corvid app: {step.Description}{(step.Elapsed is { } took ? $" ({took.TotalSeconds:0.0} s)" : "")}")))
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The host could not even be built (corvid.json unreadable, a service that cannot be made): the window still
                // opens, and every area says why.
                var reason = "Corvid cannot start: " + exception.Message;
                Console.Error.WriteLine("corvid app: " + reason);
                services = new UnavailableAppServices(reason);
            }
            if (engine is not null)
            {
                if (engine.StartFailure is { } failure)
                    Console.Error.WriteLine("corvid app: " + failure);
                // Missing models open First run, unless the command line named a page (an area's builder looks at its own page).
                if (!engine.MissingModels.IsEmpty && options.Page is null)
                    options = options with { Page = Area.FirstRun };
                services = new EngineAppServices(engine, Corvid.Host.Configuration.CorvidPaths.Home);
            }
        }

        // CORVID_APP_RENDERER=software draws with Skia on the CPU instead of OpenGL. The visual harness sets it: under Xvfb,
        // Uno's OpenGL path sometimes keeps the old height after the window shrinks and draws the frame offset.
        if (string.Equals(Environment.GetEnvironmentVariable("CORVID_APP_RENDERER"), "software", StringComparison.OrdinalIgnoreCase))
            FeatureConfiguration.Rendering.UseOpenGLOnX11 = false;
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(options, services, attachedDispatcher))
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();
        host.Run();
        // The window is gone and the UI thread with it: stop the runtime (its sessions, MCP servers, background indexing).
        engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // Attached: close the connection; the engine there goes on (its turns, its approvals' deadlines).
        attached?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Progress as it is reported, from the pool: standard error is safe from any thread.</summary>
    private sealed class Progress(Action<Corvid.Host.Composition.StartupStep> report) : IProgress<Corvid.Host.Composition.StartupStep>
    {
        public void Report(Corvid.Host.Composition.StartupStep value) => report(value);
    }
}
