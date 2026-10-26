using System.Net;
using System.Net.Sockets;
using Corvid.App.Core.Remote;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Corvid.Cli;

/// <summary>Where <see cref="AttachEndpoint"/> listens and what it serves beside the socket.</summary>
/// <param name="Socket">The Unix socket, or null for none.</param>
/// <param name="Listen">A WebSocket address (<c>ws://127.0.0.1:8737</c>), or null for none.</param>
/// <param name="Web">A directory of static files (the app's WebAssembly build) served on <paramref name="Listen"/>, or null.</param>
internal sealed record AttachEndpointOptions(string? Socket, Uri? Listen, string? Web);

/// <summary>
/// Attached mode's listener (G-1) for <c>corvid serve --attach</c>: the WebSocket at <see cref="AttachedProtocol.Path"/> on a
/// Unix socket (0600, in a 0700 directory: the default) and, when asked, on a TCP address, with the app's WebAssembly build
/// beside it. What a connection may do once it has a token is <see cref="AttachedServer"/>'s; this decides who may open one:
/// <list type="bullet">
/// <item>On a loopback address only loopback host names (no DNS rebinding of a web page onto it).</item>
/// <item>A browser's <c>Origin</c> must be this server's own (the WebAssembly build it serves), so another site open in the
/// same browser cannot script a connection; native clients send none.</item>
/// <item>Messages are read whole up to <see cref="AttachedProtocol.MaxRequestBytes"/>.</item>
/// </list>
/// </summary>
internal static class AttachEndpoint
{
    public static WebApplication Build(AttachedServer server, AttachEndpointOptions options, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Socket is null && options.Listen is null)
            throw new ArgumentException("Attached mode needs a Unix socket or an address to listen on.", nameof(options));
        var loopback = options.Listen is { } url && IsLoopback(url);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 64 * 1024;
            if (options.Socket is { } socket)
            {
                PrepareSocket(socket);
                kestrel.ListenUnixSocket(socket, listen => listen.Protocols = HttpProtocols.Http1);
            }
            if (options.Listen is { } listen)
                kestrel.Listen(Address(listen), listen.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (Refusal(context, loopback) is { } refusal)
            {
                context.Response.StatusCode = refusal.Status;
                await context.Response.WriteAsync(refusal.Message, context.RequestAborted).ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Map(AttachedProtocol.Path, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Attached mode speaks WebSocket here.", context.RequestAborted).ConfigureAwait(false);
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await using var channel = new WebSocketMessageChannel(socket, AttachedProtocol.MaxRequestBytes);
            var peer = context.Connection.RemoteIpAddress is { } ip ? ip.ToString() : AttachedAuthenticator.SocketPeer;
            await server.ServeAsync(channel, peer, stopping).ConfigureAwait(false);
        });
        if (options.Web is { } web)
        {
            var files = new PhysicalFileProvider(Path.GetFullPath(web));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".wasm"] = "application/wasm";
            types.Mappings[".dat"] = "application/octet-stream";
            types.Mappings[".blat"] = "application/octet-stream";
            types.Mappings[".dll"] = "application/octet-stream";
            types.Mappings[".pdb"] = "application/octet-stream";
            types.Mappings[".webcil"] = "application/octet-stream";
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });
        }
        return app;
    }

    /// <summary>The TCP address the endpoint listens on once started (the port is chosen when the address gives 0), or null.</summary>
    public static Uri? ListeningOn(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        // Kestrel names a Unix socket http://unix:/path; only the TCP one is an address to give.
        return addresses.Where(address => address.StartsWith("http://", StringComparison.Ordinal) && !address.StartsWith("http://unix:", StringComparison.Ordinal))
            .Select(address => new Uri(address)).FirstOrDefault();
    }

    /// <summary>Whether an address is this machine's own.</summary>
    public static bool IsLoopback(Uri url) =>
        url.IsLoopback || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    /// <summary>Reads <c>ws://host:port</c>, <c>http://host:port</c> or <c>host:port</c>.</summary>
    public static bool TryParseListen(string text, out Uri url, out string? error)
    {
        url = null!;
        error = null;
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "ws://" + trimmed;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("ws" or "http") || parsed.Port <= 0 && parsed.Port != 0
            || parsed.AbsolutePath is not ("" or "/") || !string.IsNullOrEmpty(parsed.Query))
        {
            error = $"'{text}' is not an address to listen on: ws://127.0.0.1:8737.";
            return false;
        }
        if (!parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && !IPAddress.TryParse(parsed.Host.Trim('[', ']'), out _))
        {
            error = $"Listen on an IP address or localhost, not '{parsed.Host}'.";
            return false;
        }
        url = parsed;
        return true;
    }

    internal sealed record RefusalReason(int Status, string Message);

    internal static RefusalReason? Refusal(HttpContext context, bool loopback)
    {
        var request = context.Request;
        var overTcp = context.Connection.RemoteIpAddress is not null;
        if (overTcp && loopback && !IsLoopbackHost(request.Host.Host))
            return new RefusalReason(StatusCodes.Status421MisdirectedRequest, "This endpoint serves loopback host names only (localhost, 127.0.0.1, [::1]).");
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            // A browser names the page that opens the socket: only this server's own pages (the WebAssembly build it serves).
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var from) || from.Scheme is not ("http" or "https")
                || !string.Equals(from.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase))
                return new RefusalReason(StatusCodes.Status403Forbidden, "Attached mode accepts a browser only from the pages it serves itself.");
        }
        return null;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    private static IPAddress Address(Uri url) =>
        url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
        : IPAddress.TryParse(url.Host.Trim('[', ']'), out var parsed) ? parsed
        : throw new ArgumentException($"Listen on an IP address or localhost, not '{url.Host}'.");

    /// <summary>
    /// The socket's directory made (0700) when it does not exist, and a socket left by a server that died removed (one that still answers is another
    /// server's, and is left: Kestrel's bind then fails with the reason).
    /// </summary>
    private static void PrepareSocket(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Made 0700 when new; a directory the person named is left as it is (the socket itself is made 0600 after the bind).
        PrivateFiles.EnsureDirectory(directory);
        if (!File.Exists(path))
            return;
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException)
        {
            File.Delete(path);
        }
    }

    /// <summary>After the bind: the socket itself 0600, so only its owner can connect even if the directory is opened up.</summary>
    public static void Secure(string socket)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(socket))
            File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
