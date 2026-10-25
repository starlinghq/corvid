using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Host.Audit;
using Corvid.Host.Commands;
using Corvid.Host.Configuration;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid egress</c> (G-9): the egress ledger, every request the privacy egress gate decided on for a gated model (each
/// cloud model by default), newest first; <c>corvid egress show &lt;n&gt;</c> prints one request as it was sent, after
/// redaction, with the gate's placeholders where values were. Read-only: it never starts the runtime and never creates
/// the ledger. What the model or a tool wrote is printed with its hidden characters made visible.
/// </summary>
internal static class EgressCommand
{
    public const string UsageText = "usage: corvid egress [list] [--session <id>] [--limit <n>] | corvid egress show <n>";

    public static int Run(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var positional = arguments.Positional("--session", "--limit");
        var known = new HashSet<string>(StringComparer.Ordinal) { "--session", "--limit" };
        if (arguments.Arguments.FirstOrDefault(argument => argument.StartsWith("--", StringComparison.Ordinal) && !known.Contains(argument)) is not null)
            return Fail(console, UsageText);
        var ledger = new EgressLedger(CorvidPaths.EgressDatabase(ModelCommand.Options(host)));
        switch (positional)
        {
            case [] or ["list"]:
                var limit = 50;
                if (arguments.Flag("--limit") && (!int.TryParse(arguments.Option("--limit"), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1))
                    return Fail(console, "--limit is a positive number.");
                if (arguments.Flag("--session") && string.IsNullOrWhiteSpace(arguments.Option("--session")))
                    return Fail(console, UsageText);
                return List(console, ledger, arguments.Option("--session"), limit, cancellationToken);
            case ["show", var number] when !arguments.Flag("--session") && !arguments.Flag("--limit"):
                return Show(console, ledger, number);
            default:
                return Fail(console, UsageText);
        }
    }

    private static int List(IAnsiConsole console, EgressLedger ledger, string? session, int limit, CancellationToken cancellationToken)
    {
        var entries = ledger.List(session, limit, cancellationToken);
        if (entries.Count == 0)
        {
            console.MarkupLine(session is null
                ? $"Nothing has gone to a gated model: the egress ledger ({Markup.Escape(ledger.Path)}) is empty."
                : $"Nothing in session {Markup.Escape(TerminalText.Visible(session))} went to a gated model.");
            return ExitCodes.Success;
        }
        // One line per request, so a narrow terminal wraps it whole rather than splitting every column.
        foreach (var entry in entries)
        {
            var outcome = entry.Outcome switch
            {
                EgressOutcomes.Refused => "[red]refused [/]",
                EgressOutcomes.Redacted => "[yellow]redacted[/]",
                _ => "sent    ",
            };
            console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                $"{entry.Number,5}  {entry.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {outcome}  {entry.Placeholders,3} values  {(entry.Left ? Size(entry.Bytes) : "-"),8}  ") +
                $"{Markup.Escape(entry.Slot)} → {Markup.Escape(entry.Model)}  [grey]{Markup.Escape(TerminalText.Visible(entry.SessionId ?? "(no session)"))}[/]");
        }
        console.MarkupLine("[grey]corvid egress show <#> prints a request as it was sent.[/]");
        return ExitCodes.Success;
    }

    private static int Show(IAnsiConsole console, EgressLedger ledger, string number)
    {
        if (ledger.Find(number) is not { } entry)
            return Fail(console, $"The egress ledger has no request {number}.", ExitCodes.Failure);
        console.MarkupLine($"[bold]request {entry.Number}[/] → {Markup.Escape(entry.Model)} ({Markup.Escape(entry.Slot)})" +
            (entry.Host is { } host ? $" at {Markup.Escape(host)}" : "") + (entry.Secret is { } secret ? $" via Vault [grey]{Markup.Escape(secret)}[/]" : ""));
        console.MarkupLine($"{entry.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} · session " +
            $"{Markup.Escape(TerminalText.Visible(entry.SessionId ?? "(none)"))} · {entry.Outcome} · {entry.Placeholders} " +
            $"{(entry.Placeholders == 1 ? "value" : "values")} redacted · {Markup.Escape(TerminalText.Visible(entry.Reason))}");
        if (!entry.Left)
        {
            console.MarkupLine("[grey]Refused before it left: nothing of it was sent, and nothing of it is kept.[/]");
            return ExitCodes.Success;
        }
        if (ledger.Render(entry.RequestId) is not { } full)
        {
            console.MarkupLine("[yellow]The request as sent was not kept (the egress store could not be written).[/]");
            return ExitCodes.Success;
        }
        console.WriteLine();
        // Plain text, never markup: what the model and the tools wrote, hidden characters shown.
        console.WriteLine(TerminalText.Visible(full));
        return ExitCodes.Success;
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1000.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000.0:0.0} MB"),
    };

    private static int Fail(IAnsiConsole console, string message, int code = ExitCodes.Usage)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return code;
    }
}
