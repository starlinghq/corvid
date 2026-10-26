using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Engine;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid serve</c>: the OpenAI-compatible endpoint (plan §12) until Ctrl-C. Loopback only unless a token secret is
/// configured; the token is checked through Corvid Vault, which must hold it when the endpoint starts.
/// <para>
/// With <c>--attach</c> (G-1) it also serves attached mode: the app's session seam (the engine's session service, the same
/// code the desktop window runs in-process) for an app or a browser elsewhere, on a Unix socket by default and on a loopback
/// WebSocket with <c>--listen</c>. Every connection needs a token. The owner token is the Vault secret
/// <c>Corvid:Serve:Attach:TokenSecret</c> names when it is configured, and otherwise one generated on first use in a 0600
/// file (<see cref="AttachedPaths.OwnerTokenFile"/>); a second device pairs with a code (<c>corvid serve pair</c>, or the
/// owner's window) and gets its own token, kept as a digest. Listening beyond loopback needs the Vault secret.
/// <c>corvid serve devices</c> lists the paired devices and <c>corvid serve revoke &lt;name&gt;</c> removes one.
/// </para>
/// </summary>
internal static class ServeCommand
{
    /// <summary>The verbs that work on attached mode's files or a running server and load no models.</summary>
    public static bool LoadsModels(CliArguments arguments) => arguments.Command == "serve" && Verb(arguments) is [];

    private static string[] Verb(CliArguments arguments) => arguments.Positional("--listen", "--socket", "--web", "--name");

    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        switch (Verb(arguments))
        {
            case []:
                return await ServeAsync(host, arguments, context, cancellationToken).ConfigureAwait(false);
            case ["pair"]:
                return await PairAsync(host, arguments, console, cancellationToken).ConfigureAwait(false);
            case ["devices"]:
                return Devices(console);
            case ["revoke", var name]:
                return Revoke(console, name);
            default:
                console.MarkupLine("[red]corvid serve takes no argument, or pair, devices or revoke <name>.[/]");
                return ExitCodes.Usage;
        }
    }

    private static async Task<int> ServeAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var attach = arguments.Flag("--attach");
        var openAI = !arguments.Flag("--no-openai");
        if (!openAI && !attach)
        {
            console.MarkupLine("[red]--no-openai leaves nothing to serve without --attach.[/]");
            return ExitCodes.Usage;
        }
        await CorvidCli.StartAsync(host, cancellationToken).ConfigureAwait(false);
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var settings = options.Serve;
        var vaultSocket = options.Vault.SocketPath is { Length: > 0 } socketPath ? CorvidPaths.ExpandHome(socketPath) : null;

        VaultTokenVerifier? verifier = null;
        if (openAI && settings.TokenSecret is { Length: > 0 } secret)
        {
            verifier = new VaultTokenVerifier(secret, vaultSocket);
            if (await CheckSecretAsync(console, verifier, "Corvid:Serve:TokenSecret", cancellationToken).ConfigureAwait(false) is { } failed)
                return failed;
        }

        AttachPlan? plan = null;
        if (attach)
        {
            plan = await PlanAttachAsync(console, arguments, settings.Attach, vaultSocket, cancellationToken).ConfigureAwait(false);
            if (plan is null)
                return ExitCodes.Usage;
        }

        CorvidRuntime runtime;
        try
        {
            runtime = await host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelsUnavailableException exception)
        {
            ModelCommand.ReportMissing(console, exception.Missing);
            return ExitCodes.ModelsMissing;
        }

        // With attached mode the app engine holds the runtime and stops it; without, this does.
        AppEngine? engine = plan is null ? null : AppEngine.ForRuntime(host, runtime);
        using var dispatcher = plan is null ? null : new SerialDispatcher("corvid serve: attached", exception =>
            console.MarkupLine($"[red]attach: {Markup.Escape(exception.Message)}[/]"));
        EngineSessionService? sessions = engine is null ? null : new EngineSessionService(engine, dispatcher!, TimeProvider.System);
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void OnInterrupt() => stop.Cancel();
            context.Interrupts.Interrupted += OnInterrupt;
            // SIGTERM (systemd, kill) stops it as Ctrl-C does, so the socket and the sessions are let go of.
            using var terminate = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, signal =>
                {
                    signal.Cancel = true;
                    stop.Cancel();
                });
            WebApplication? openAIApp = null;
            WebApplication? attachApp = null;
            var boundSocket = false;
            try
            {
                if (openAI)
                {
                    openAIApp = OpenAIEndpoint.Build(runtime, new OpenAIEndpointOptions(settings, verifier));
                    try
                    {
                        await openAIApp.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        console.MarkupLine($"[red]Cannot listen on {Markup.Escape(settings.Url)}: {Markup.Escape(exception.Message)}[/]");
                        return ExitCodes.Failure;
                    }
                    var address = OpenAIEndpoint.ListeningOn(openAIApp).ToString().TrimEnd('/');
                    console.MarkupLine($"[bold]corvid serve[/] on [bold]{Markup.Escape(address)}/v1[/] (model '{OpenAIEndpoint.ModelId}', " +
                        $"{(verifier is null ? "loopback callers only" : "bearer token checked through Vault")}, approvals: {settings.Approvals}). " +
                        "Ctrl-C stops.");
                }
                if (plan is not null)
                {
                    var server = new AttachedServer(sessions!, plan.Authenticator)
                    {
                        Log = line => console.MarkupLine(Markup.Escape(line)),
                        // A turn never waits on a client that went: with none left, its in-turn questions are put aside (they stay
                        // open for the next message) and its approvals keep their deadline.
                        NoClientsLeft = sessions!.PutAsideInTurnQuestions,
                    };
                    sessions.AnswererPresent = () => server.Clients > 0;
                    attachApp = AttachEndpoint.Build(server, new AttachEndpointOptions(plan.Socket, plan.Listen, plan.Web), stop.Token);
                    try
                    {
                        await attachApp.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        console.MarkupLine($"[red]Attached mode cannot listen: {Markup.Escape(exception.Message)}[/]");
                        return ExitCodes.Failure;
                    }
                    if (plan.Socket is { } socket)
                    {
                        boundSocket = true;
                        AttachEndpoint.Secure(socket);
                    }
                    var where = new List<string>();
                    if (plan.Socket is { } unix)
                        where.Add("unix:" + unix);
                    if (AttachEndpoint.ListeningOn(attachApp) is { } tcp)
                        where.Add($"ws://{tcp.Authority}{AttachedProtocol.Path}" + (plan.Web is null ? "" : $" (the app at http://{tcp.Authority}/)"));
                    console.MarkupLine($"[bold]attached mode[/] on [bold]{Markup.Escape(string.Join(", ", where))}[/]; owner token: {Markup.Escape(plan.TokenSource)}. " +
                        "Pair another device with [bold]corvid serve pair[/].");
                }
                foreach (var notice in runtime.DeciderNotices)
                    console.MarkupLine($"[yellow]{Markup.Escape(notice)}[/]");
                try
                {
                    await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            finally
            {
                context.Interrupts.Interrupted -= OnInterrupt;
                if (attachApp is not null)
                {
                    await attachApp.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await attachApp.DisposeAsync().ConfigureAwait(false);
                }
                if (openAIApp is not null)
                {
                    await openAIApp.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    await openAIApp.DisposeAsync().ConfigureAwait(false);
                }
                // Only a socket this process bound: one that failed to bind is another server's.
                if (boundSocket && plan?.Socket is { } leftover)
                    TryDelete(leftover);
            }
        }
        finally
        {
            sessions?.Dispose();
            if (engine is not null)
                await engine.DisposeAsync().ConfigureAwait(false);
            else
            {
                await runtime.MaintenanceIdleAsync().ConfigureAwait(false);
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }
        return ExitCodes.Success;
    }

    /// <summary>Where attached mode listens and who its owner is.</summary>
    private sealed record AttachPlan(string? Socket, Uri? Listen, string? Web, AttachedAuthenticator Authenticator, string TokenSource);

    private static async Task<AttachPlan?> PlanAttachAsync(IAnsiConsole console, CliArguments arguments, AttachSettings settings, string? vaultSocket,
        CancellationToken cancellationToken)
    {
        var socketText = arguments.Option("--socket") ?? settings.Socket;
        string? socket = socketText is { Length: > 0 } given
            ? given.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(CorvidPaths.ExpandHome(given))
            : AttachedPaths.DefaultSocket;
        Uri? listen = null;
        if ((arguments.Option("--listen") ?? settings.Listen) is { Length: > 0 } listenText)
        {
            if (!AttachEndpoint.TryParseListen(listenText, out var url, out var error))
            {
                console.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
                return null;
            }
            listen = url;
        }
        if (socket is null && listen is null)
        {
            console.MarkupLine("[red]Attached mode needs somewhere to listen: leave the Unix socket on, or give --listen ws://127.0.0.1:8737.[/]");
            return null;
        }
        var web = arguments.Option("--web") ?? settings.Web;
        if (web is { Length: > 0 })
        {
            web = Path.GetFullPath(CorvidPaths.ExpandHome(web));
            if (listen is null || !File.Exists(Path.Combine(web, "index.html")))
            {
                console.MarkupLine($"[red]--web needs --listen and a directory with the app's index.html ({Markup.Escape(web)}).[/]");
                return null;
            }
        }
        else
        {
            web = null;
        }

        Func<string, CancellationToken, ValueTask<bool?>> owner;
        string source;
        if (settings.TokenSecret is { Length: > 0 } secret)
        {
            var verifier = new VaultTokenVerifier(secret, vaultSocket);
            if (await CheckSecretAsync(console, verifier, "Corvid:Serve:Attach:TokenSecret", cancellationToken).ConfigureAwait(false) is not null)
                return null;
            owner = verifier.VerifyAsync;
            source = $"the Vault secret '{secret}'";
        }
        else
        {
            if (listen is not null && !AttachEndpoint.IsLoopback(listen))
            {
                console.MarkupLine("[red]Attached mode listens beyond loopback only with a Vault-held owner token: set Corvid:Serve:Attach:TokenSecret " +
                    "to a secret stored with corvid-vault set.[/]");
                return null;
            }
            string token;
            try
            {
                token = OwnerTokens.Ensure(AttachedPaths.OwnerTokenFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                console.MarkupLine($"[red]The owner token cannot be read or made: {Markup.Escape(exception.Message)}[/]");
                return null;
            }
            owner = (presented, _) => ValueTask.FromResult<bool?>(AttachedSecrets.FixedTimeEquals(presented, token));
            source = $"generated in {AttachedPaths.OwnerTokenFile} (0600; Vault holds none: Corvid:Serve:Attach:TokenSecret is not set)";
        }
        return new AttachPlan(socket, listen, web, new AttachedAuthenticator(owner, AttachedPaths.DevicesFile), source);
    }

    private static async Task<int?> CheckSecretAsync(IAnsiConsole console, VaultTokenVerifier verifier, string key, CancellationToken cancellationToken)
    {
        bool exists;
        try
        {
            exists = await verifier.SecretExistsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            console.MarkupLine($"[red]Corvid Vault is not answering ({Markup.Escape(exception.Message)}); the token cannot be checked, so nothing is served.[/]");
            return ExitCodes.Failure;
        }
        if (exists)
            return null;
        console.MarkupLine($"[red]Corvid Vault holds no secret named '{Markup.Escape(verifier.Secret)}' ({key}).[/] " +
            $"Store one with [bold]corvid-vault set {Markup.Escape(verifier.Secret)}[/] (a random value of at least {VaultTokenVerifier.MinimumBytes} bytes).");
        return ExitCodes.Failure;
    }

    /// <summary>
    /// <c>corvid serve pair</c>: asks the running server (over its Unix socket, with the owner token) for a pairing code and
    /// prints it, with how long it lasts. The code never touches a file.
    /// </summary>
    private static async Task<int> PairAsync(IHost host, CliArguments arguments, IAnsiConsole console, CancellationToken cancellationToken)
    {
        var settings = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value.Serve.Attach;
        var socketText = arguments.Option("--socket") ?? settings.Socket;
        var socket = socketText is { Length: > 0 } given && !given.Equals("off", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(CorvidPaths.ExpandHome(given))
            : AttachedPaths.DefaultSocket;
        string? token;
        try
        {
            token = OwnerTokens.Find(AttachedPaths.OwnerTokenFile);
        }
        catch (UnauthorizedAccessException exception)
        {
            console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return ExitCodes.Failure;
        }
        if (token is null)
        {
            console.MarkupLine($"[red]No owner token to pair with: start [bold]corvid serve --attach[/] first, or set {OwnerTokens.EnvironmentVariable} " +
                "when the owner token is a Vault secret.[/]");
            return ExitCodes.Failure;
        }
        if (!AttachedAddress.TryParse("unix:" + socket, out var address, out var error))
        {
            console.MarkupLine($"[red]{Markup.Escape(error!)}[/]");
            return ExitCodes.Usage;
        }
        try
        {
            await using var client = await AttachedClient.ConnectAsync(address, token, "corvid serve pair", new InlineDispatcher(), cancellationToken)
                .ConfigureAwait(false);
            var code = await client.CreatePairingCodeAsync(cancellationToken).ConfigureAwait(false);
            var minutes = Math.Max(1, (int)Math.Round((code.Expires - DateTimeOffset.UtcNow).TotalMinutes));
            console.MarkupLine($"Pairing code: [bold]{Markup.Escape(code.Code)}[/] (redeems once, within {minutes} min). " +
                "Give it to the device: Corvid.App --attach <address> --pair <code>, or the app's page in a browser with #pair=<code> after its address.");
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            console.MarkupLine($"[red]Cannot get a pairing code from {Markup.Escape(address.Display)}: {Markup.Escape(exception.Message)}[/]");
            return ExitCodes.Failure;
        }
    }

    private static int Devices(IAnsiConsole console)
    {
        IReadOnlyList<PairedDevice> devices;
        try
        {
            devices = AttachedAuthenticator.ReadDevices(AttachedPaths.DevicesFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return ExitCodes.Failure;
        }
        if (devices.Count == 0)
        {
            console.MarkupLine("No device is paired.");
            return ExitCodes.Success;
        }
        foreach (var device in devices)
            console.MarkupLine($"{Markup.Escape(Corvid.App.Core.SafeText.Clean(device.Name))}  paired {device.Paired.ToLocalTime():yyyy-MM-dd HH:mm}");
        return ExitCodes.Success;
    }

    private static int Revoke(IAnsiConsole console, string name)
    {
        try
        {
            if (AttachedAuthenticator.Revoke(AttachedPaths.DevicesFile, name))
            {
                console.MarkupLine($"Revoked '{Markup.Escape(name)}': its token is refused from its next connection.");
                return ExitCodes.Success;
            }
            console.MarkupLine($"[red]No paired device is named '{Markup.Escape(name)}'.[/] corvid serve devices lists them.");
            return ExitCodes.Failure;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return ExitCodes.Failure;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
