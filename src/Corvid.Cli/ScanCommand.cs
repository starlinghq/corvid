using System.Text;
using System.Text.Json;
using Corvid.Host.Audit;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Corvid.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid scan</c> (C-023): the built-in detection rules over the event log, post-hoc. Read-only: it never starts the host,
/// writes the log or creates it. Findings are grouped by session, with whether the turn loop already recorded the match inline.
/// </summary>
internal static class ScanCommand
{
    public const string UsageText = "usage: corvid scan [--session <id>] [--fail-on info|low|medium|high|critical] [--json]";

    public static int Run(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var session = arguments.Option("--session");
        var failOn = arguments.Option("--fail-on");
        if (arguments.Positional("--session", "--fail-on").Length > 0
            || arguments.Arguments.Any(argument => argument.StartsWith("--", StringComparison.Ordinal) && argument is not ("--session" or "--fail-on" or "--json"))
            || arguments.Flag("--session") && string.IsNullOrEmpty(session)
            || arguments.Flag("--fail-on") && (failOn is null || RuleSeverity.Rank(failOn) < 0))
        {
            console.MarkupLine($"[red]{Markup.Escape(UsageText)}[/]");
            return ExitCodes.Usage;
        }
        var json = arguments.Flag("--json");

        // The log's constructor creates a database that is not there; a scan must not.
        var path = CorvidPaths.EventLog(options);
        if (!File.Exists(path))
        {
            console.MarkupLine($"[red]No event log at {Markup.Escape(path)}[/] (Corvid:DataDirectory); nothing to scan.");
            return ExitCodes.Failure;
        }

        DetectionEngine engine;
        try
        {
            engine = LogScan.CreateEngine(options);
        }
        catch (RuleLoadException exception)
        {
            console.MarkupLine($"[red]The detection rules did not load:[/] {Markup.Escape(exception.Message)}");
            return ExitCodes.Failure;
        }

        LogScanResult scan;
        try
        {
            var log = new SqliteEventLog(path);
            if (session is not null && !log.ListSessions(cancellationToken).Contains(session, StringComparer.Ordinal))
            {
                console.MarkupLine($"[red]No session '{Markup.Escape(session)}' in {Markup.Escape(path)}.[/]");
                return ExitCodes.Failure;
            }
            scan = LogScan.Run(log, engine, session is null ? null : [session], cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                                              or Microsoft.Data.Sqlite.SqliteException)
        {
            console.MarkupLine($"[red]The event log could not be read:[/] {Markup.Escape(exception.Message)}");
            return ExitCodes.Failure;
        }

        if (json)
            WriteJson(console, scan);
        else
            WriteText(console, scan);
        return scan.ExitCode(failOn);
    }

    private static void WriteText(IAnsiConsole console, LogScanResult scan)
    {
        var (pack, sessions) = (scan.Pack, scan.Sessions.Length);
        IReadOnlyList<DetectionFinding> findings = scan.Findings;
        IReadOnlyList<DetectionError> errors = scan.Errors;
        console.MarkupLine($"[bold]corvid scan[/] · {pack.Rules.Length} rules · {sessions} session{(sessions == 1 ? "" : "s")} · " +
            $"{findings.Count} finding{(findings.Count == 1 ? "" : "s")}");
        foreach (var group in findings.GroupBy(finding => finding.SessionId, StringComparer.Ordinal))
        {
            console.MarkupLine($"session [bold]{Markup.Escape(group.Key)}[/]");
            foreach (var finding in group.OrderByDescending(finding => RuleSeverity.Rank(finding.Rule.Severity)).ThenBy(finding => finding.Rule.Id, StringComparer.Ordinal))
            {
                var rule = finding.Rule;
                console.MarkupLine($"  [{Colour(rule.Severity)}]{rule.Severity,-8}[/] {Markup.Escape(rule.Id)} v{rule.Version} ({finding.Posture}): {Markup.Escape(rule.Title)}");
                var events = finding.Sequences.IsEmpty ? "" : $" · events {string.Join(", ", finding.Sequences)}";
                var inline = scan.RecordedInline(finding) ? " · recorded inline" : "";
                console.MarkupLine($"           [grey]{Markup.Escape(rule.Reason)}{events}{inline}[/]");
            }
        }
        foreach (var error in errors)
            console.MarkupLine($"[red]{Markup.Escape(error.Rule.Id)} could not be evaluated in session {Markup.Escape(error.SessionId)}:[/] {Markup.Escape(error.Message)}");
    }

    private static string Colour(string severity) => severity switch
    {
        RuleSeverity.Critical => "red bold",
        RuleSeverity.High => "red",
        RuleSeverity.Medium => "yellow",
        RuleSeverity.Low => "blue",
        _ => "grey",
    };

    // One JSON document, written to the terminal's raw writer so nothing wraps it.
    private static void WriteJson(IAnsiConsole console, LogScanResult scan)
    {
        var (pack, sessions) = (scan.Pack, scan.Sessions.Length);
        IReadOnlyList<DetectionFinding> findings = scan.Findings;
        IReadOnlyList<DetectionError> errors = scan.Errors;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("rules", pack.Rules.Length);
            writer.WriteNumber("sessions", sessions);
            writer.WriteStartArray("findings");
            foreach (var finding in findings)
            {
                var rule = finding.Rule;
                writer.WriteStartObject();
                writer.WriteString("session", finding.SessionId);
                writer.WriteString("rule_id", rule.Id);
                writer.WriteNumber("version", rule.Version);
                writer.WriteString("title", rule.Title);
                writer.WriteString("category", rule.Category);
                writer.WriteString("severity", rule.Severity);
                writer.WriteString("posture", finding.Posture);
                writer.WriteString("reason", rule.Reason);
                writer.WriteStartArray("sequences");
                foreach (var sequence in finding.Sequences)
                    writer.WriteNumberValue(sequence);
                writer.WriteEndArray();
                writer.WriteBoolean("recorded_inline", scan.RecordedInline(finding));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("errors");
            foreach (var error in errors)
            {
                writer.WriteStartObject();
                writer.WriteString("session", error.SessionId);
                writer.WriteString("rule_id", error.Rule.Id);
                writer.WriteString("message", error.Message);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var output = console.Profile.Out.Writer;
        output.Write(Encoding.UTF8.GetString(buffer.ToArray()));
        output.WriteLine();
        output.Flush();
    }
}
