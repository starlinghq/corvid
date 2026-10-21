using System.Globalization;
using Corvid.Gateway;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid gateway</c> (plan §12, §17 row 1.2): run the messaging gateway until Ctrl-C, show its state, and pair people.
/// Pairing is approved here, on the machine, never from a chat.
/// </summary>
internal static class GatewayCommand
{
    public const string UsageText = "usage: corvid gateway [run] | status | pair [list] | pair approve <code> --owner|--guest | pair revoke <adapter> <user>";

    /// <summary>Whether this command line runs the gateway (and so loads models and needs the hosted service).</summary>
    public static bool Runs(CliArguments arguments) => arguments.Command == "gateway" && arguments.Positional() is [] or ["run"];

    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        // Validated here (ValidateOnStart runs only when the host starts): an invalid file fails before anything opens.
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        return arguments.Positional() switch
        {
            [] or ["run"] => await RunGatewayAsync(host, context, cancellationToken).ConfigureAwait(false),
            ["status"] => Status(console, options),
            ["pair"] or ["pair", "list"] => ListPairing(console, options),
            ["pair", "approve", var code] => Approve(console, options, code, arguments.Flag("--owner"), arguments.Flag("--guest")),
            ["pair", "revoke", var adapter, var user] => Revoke(console, options, adapter, user),
            _ => Usage(console),
        };
    }

    private static int Usage(IAnsiConsole console)
    {
        console.MarkupLine($"[red]{Markup.Escape(UsageText)}[/]");
        return ExitCodes.Usage;
    }

    private static async Task<int> RunGatewayAsync(IHost host, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var service = host.Services.GetRequiredService<GatewayService>();
        var output = new Lock();
        service.Notice = message =>
        {
            lock (output)
                console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        };
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInterrupt() => stop.TrySetResult();
        context.Interrupts.Interrupted += OnInterrupt;
        try
        {
            await CorvidCli.StartAsync(host, cancellationToken).ConfigureAwait(false);
            try
            {
                var (runtime, gateway) = await service.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (output)
                {
                    console.MarkupLine($"[bold]corvid gateway[/] · {Markup.Escape(string.Join(", ", gateway.Adapters.Select(adapter => adapter.Name)))} · " +
                        "Ctrl-C stops");
                    foreach (var notice in runtime.DeciderNotices)
                        console.MarkupLine($"[yellow]{Markup.Escape(notice)}[/]");
                }
            }
            catch (ModelsUnavailableException exception)
            {
                ModelCommand.ReportMissing(console, exception.Missing);
                return ExitCodes.ModelsMissing;
            }
            catch (InvalidOperationException exception)
            {
                console.MarkupLine($"[red]The gateway did not start:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.Failure;
            }
            await stop.Task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            console.MarkupLine("[grey]Stopping: answers still being written are queued and sent on the next start.[/]");
            return ExitCodes.Success;
        }
        finally
        {
            context.Interrupts.Interrupted -= OnInterrupt;
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static int Status(IAnsiConsole console, CorvidOptions options)
    {
        var enabled = CorvidGateway.Enabled(options);
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumns("adapter", "token secret", "owners", "guests", "home chat");
        foreach (var name in enabled)
        {
            var settings = options.Gateway.Adapters.First(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            table.AddRow(name, settings.Secret + (settings.AppSecret is { } app ? ", " + app : ""), string.Join(", ", settings.Owners),
                string.Join(", ", settings.Guests), settings.HomeChat ?? "");
        }
        if (enabled.Count == 0)
            console.MarkupLine("[yellow]No adapter is enabled[/] (Corvid:Gateway:Adapters:<telegram|discord|slack>:Enabled).");
        else
            console.Write(table);

        var store = new PairingStore(CorvidPaths.GatewayDatabase(options));
        console.MarkupLine(Running(store) ? "[green]A gateway is running.[/]" : "[grey]No gateway is running.[/]");
        var ledger = new DeliveryLedger(new SqliteEventLog(CorvidPaths.EventLog(options)));
        ledger.Catch(long.TryParse(store.GetState(GatewayHost.LowWaterKey), NumberStyles.None, CultureInfo.InvariantCulture, out var mark) ? mark : 0);
        var open = ledger.Pending;
        console.MarkupLine(open.Count == 0 ? "No deliveries are waiting." : $"{open.Count} deliver{(open.Count == 1 ? "y is" : "ies are")} waiting: " +
            Markup.Escape(string.Join(", ", open.GroupBy(pending => pending.Delivery.Adapter).Select(group => $"{group.Key} {group.Count()}"))));
        console.MarkupLine($"{store.Users().Count} paired, {store.Pending().Count} waiting for approval (corvid gateway pair).");
        return ExitCodes.Success;
    }

    // The gateway holds a lock beside gateway.db while it runs.
    private static bool Running(PairingStore store)
    {
        try
        {
            using var probe = new FileStream(store.Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static int ListPairing(IAnsiConsole console, CorvidOptions options)
    {
        var store = new PairingStore(CorvidPaths.GatewayDatabase(options));
        var pending = store.Pending();
        if (pending.Count == 0)
            console.MarkupLine("No one is waiting to pair.");
        else
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumns("code", "adapter", "user", "name", "asked", "expires");
            foreach (var request in pending)
                table.AddRow(PairingStore.Display(request.Code), request.Adapter, request.UserId, request.Name ?? "",
                    request.CreatedAt.ToString("u", CultureInfo.InvariantCulture), request.ExpiresAt.ToString("u", CultureInfo.InvariantCulture));
            console.Write(table);
            console.MarkupLine("Approve with `corvid gateway pair approve <code> --owner` (yourself) or `--guest` (someone whose messages are outside content).");
        }
        var users = store.Users();
        if (users.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumns("adapter", "user", "name", "role", "paired");
            foreach (var user in users)
                table.AddRow(user.Adapter, user.UserId, user.Name ?? "", user.Role.ToString().ToLowerInvariant(),
                    user.PairedAt.ToString("u", CultureInfo.InvariantCulture));
            console.Write(table);
        }
        return ExitCodes.Success;
    }

    private static int Approve(IAnsiConsole console, CorvidOptions options, string code, bool owner, bool guest)
    {
        // No default: an owner's messages are the user's and only an owner runs commands and answers approvals, so approving a
        // stranger's code by habit must not make them one.
        if (owner == guest)
        {
            console.MarkupLine("[red]Say who this is:[/] --owner (you: your messages, commands and approvals) or --guest " +
                "(someone else: their messages are outside content).");
            return ExitCodes.Usage;
        }
        var store = new PairingStore(CorvidPaths.GatewayDatabase(options));
        if (store.Approve(code, guest ? SenderRole.Guest : SenderRole.Owner) is not { } user)
        {
            console.MarkupLine($"[red]No waiting pairing has the code {Markup.Escape(code)}[/] (codes expire after an hour; `corvid gateway pair` lists them).");
            return ExitCodes.Failure;
        }
        console.MarkupLine($"[green]Paired[/] {Markup.Escape(user.Adapter)} user {Markup.Escape(user.UserId)}" +
            (user.Name is { } name ? $" ({Markup.Escape(name)})" : "") + $" as {(guest ? "a guest: their messages are treated as outside content" : "the owner")}.");
        return ExitCodes.Success;
    }

    private static int Revoke(IAnsiConsole console, CorvidOptions options, string adapter, string user)
    {
        var store = new PairingStore(CorvidPaths.GatewayDatabase(options));
        if (!store.Revoke(adapter.ToLowerInvariant(), user))
        {
            console.MarkupLine($"[yellow]{Markup.Escape(adapter)} user {Markup.Escape(user)} was not paired[/] (an allowlist in the configuration is not a pairing).");
            return ExitCodes.Failure;
        }
        console.MarkupLine($"Revoked {Markup.Escape(adapter)} user {Markup.Escape(user)}.");
        return ExitCodes.Success;
    }
}
