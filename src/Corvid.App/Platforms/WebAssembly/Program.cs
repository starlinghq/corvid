using System.Runtime.InteropServices.JavaScript;
using Corvid.App.Core;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Infrastructure;
using Uno.UI.Hosting;

namespace Corvid.App;

/// <summary>
/// The WebAssembly head (built only with -p:CorvidAppWasm=true). A browser tab has no command line and runs no models, so it
/// attaches (G-1) to the Corvid that serves it (<c>corvid serve --attach --listen … --web &lt;this build&gt;</c>): the socket
/// is the page's own host's <c>/attach</c>, or <c>?attach=ws://host:port</c>. It pairs as a device with the code in the
/// address's fragment (<c>#pair=XXXXX-XXXXX</c>, from <c>corvid serve pair</c>; a fragment never reaches a server's log),
/// keeps the device token in the browser's storage, and takes the code out of the address. <c>?demo</c> opens the demo.
/// </summary>
public static class Program
{
    private const string TokenKey = "corvid.attach.token:";

    public static async Task Main(string[] args)
    {
        AppLogging.Initialize();
        var options = new AppOptions { Demo = true };
        Core.Composition.IAppServices? services = null;
        LateDispatcher? dispatcher = null;
        var location = JSHost.GlobalThis.GetPropertyAsJSObject("location");
        var href = location?.GetPropertyAsString("href");
        if (href is not null && Uri.TryCreate(href, UriKind.Absolute, out var page) && !Query(page, "demo", out _))
        {
            var address = Query(page, "attach", out var given) && given is { Length: > 0 } ? given
                : $"{(page.Scheme == "https" ? "wss" : "ws")}://{page.Authority}{AttachedProtocol.Path}";
            var code = Fragment(page, "pair");
            if (code is not null)
                location!.SetProperty("hash", "");
            dispatcher = new LateDispatcher();
            var attached = await AttachedStartup.ConnectAsync(address, code, "a browser", dispatcher, new BrowserTokenStore(), ownerTokenFile: null);
            Console.WriteLine("corvid app: " + attached.Report);
            options = new AppOptions { Attach = address };
            services = attached.Connection is null
                ? new Core.Composition.UnavailableAppServices(attached.Report +
                    " To pair this browser, run corvid serve pair on that machine and open this page again with #pair=<code> after its address.")
                : attached.Services;
        }
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App(options, services, dispatcher))
            .UseWebAssembly()
            .Build();
        await host.RunAsync();
    }

    private static bool Query(Uri page, string name, out string? value)
    {
        value = null;
        foreach (var part in page.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(equals < 0 ? part : part[..equals]);
            if (key != name)
                continue;
            value = equals < 0 ? "" : Uri.UnescapeDataString(part[(equals + 1)..]);
            return true;
        }
        return false;
    }

    private static string? Fragment(Uri page, string name)
    {
        foreach (var part in page.Fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (part.StartsWith(name + "=", StringComparison.Ordinal))
                return Uri.UnescapeDataString(part[(name.Length + 1)..]);
        return null;
    }

    /// <summary>The device token in the browser's local storage, per address (this page's origin's storage only).</summary>
    private sealed class BrowserTokenStore : IDeviceTokenStore
    {
        private static JSObject? Storage => JSHost.GlobalThis.GetPropertyAsJSObject("localStorage");

        public string? Get(string address) => Storage?.GetPropertyAsString(TokenKey + address) is { Length: > 0 } token ? token : null;

        public void Set(string address, string token) => Storage?.SetProperty(TokenKey + address, token);
    }
}
