using Corvid.App.Core;
using Corvid.App.Infrastructure;
using Uno.UI.Hosting;

namespace Corvid.App;

/// <summary>
/// The desktop head: X11 and the framebuffer on Linux, Win32 on Windows, AppKit on macOS. The command line is read
/// first, so a mistyped option prints the usage instead of opening a window.
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
        // CORVID_APP_RENDERER=software draws with Skia on the CPU instead of OpenGL. The visual harness sets it: under Xvfb,
        // Uno's OpenGL path sometimes keeps the old height after the window shrinks and draws the frame offset.
        if (string.Equals(Environment.GetEnvironmentVariable("CORVID_APP_RENDERER"), "software", StringComparison.OrdinalIgnoreCase))
            FeatureConfiguration.Rendering.UseOpenGLOnX11 = false;
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(options))
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();
        host.Run();
        return 0;
    }
}
