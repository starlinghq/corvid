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
[System.Runtime.Versioning.SupportedOSPlatform("browser")]
public static partial class Program
{
    private const string TokenKey = "corvid.attach.token:";

    public static async Task Main(string[] args)
    {
        // No AppLogging here: its console logger provider is not supported in a browser (PlatformNotSupportedException at
        // start, which left the tab on its loading bar). Uno's own browser console logging stays.
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
            // A6 review L4: the code leaves the address and the tab's history alike (setting location.hash kept the entry
            // with the code for the Back button and left a bare '#').
            if (code is not null)
                DropFragment(page);
            // A6 fix: a pairing code goes only to the server that served this page, so a crafted ?attach= link cannot carry
            // it to another one.
            if (code is not null && (!Uri.TryCreate(address, UriKind.Absolute, out var target)
                || !string.Equals(target.Authority, page.Authority, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("corvid app: a pairing code is sent only to the server of this page, not to " + address);
                code = null;
            }
            dispatcher = new LateDispatcher();
            // A6 review M3: a server that accepts and then says nothing opens the page refused after a while, never hung.
            using var connecting = new CancellationTokenSource(AttachedStartup.ConnectTimeout);
            var attached = await AttachedStartup.ConnectAsync(address, code, "a browser", dispatcher, new BrowserTokenStore(), ownerTokenFile: null,
                connecting.Token);
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

    /// <summary>Replaces the page's history entry with its address without the fragment, without loading it again.</summary>
    private static void DropFragment(Uri page)
    {
        var history = JSHost.GlobalThis.GetPropertyAsJSObject("history");
        using var replaceState = history?.GetPropertyAsJSObject("replaceState");
        if (history is null || replaceState is null)
            return;
        using (history)
            Apply(replaceState, history, [null, "", page.GetLeftPart(UriPartial.Query)]);
    }

    // Reflect.apply, so replaceState runs with history as its 'this' (a bare call of it throws "Illegal invocation").
    [JSImport("globalThis.Reflect.apply")]
    private static partial void Apply(JSObject function, JSObject self, [JSMarshalAs<JSType.Array<JSType.Any>>] object?[] arguments);

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
