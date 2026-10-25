using Corvid.Host.Audit;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Corvid.Kernel.Traces;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid traces export</c> (C-027): this user's event log as Beacon-schema JSONL, one event per line, for Beacon's
/// detections, dashboards and SIEM forwarding. It reads the log and writes nothing to it; without <c>--out</c> the lines go
/// to standard output as raw bytes, never through the console renderer.
/// </summary>
internal static class TracesCommand
{
    private const string Usage =
        "Usage: corvid traces export [--session <id> | --all] [--retention full|metadata] [--include-local-memory] [--out <file>]";

    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        if (arguments.Positional("--session", "--retention", "--out") is not ["export"])
            return Fail(console, Usage, ExitCodes.Usage);
        var session = arguments.Option("--session");
        var all = arguments.Flag("--all");
        if ((session is null) == !all)
            return Fail(console, "Give one of --session <id> or --all.", ExitCodes.Usage);
        var retention = arguments.Option("--retention") ?? "full";
        if (retention is not ("full" or "metadata"))
            return Fail(console, "--retention is full or metadata.", ExitCodes.Usage);
        var known = new HashSet<string>(StringComparer.Ordinal) { "--session", "--retention", "--out", "--all", "--include-local-memory" };
        if (arguments.Arguments.FirstOrDefault(argument => argument.StartsWith("--", StringComparison.Ordinal) && !known.Contains(argument)) is { } unknown)
            return Fail(console, $"Unknown option '{unknown}'. {Usage}", ExitCodes.Usage);

        var options = ModelCommand.Options(host);
        var path = CorvidPaths.EventLog(options);
        // Opening the log creates and migrates it; an export must not leave an empty log behind.
        if (!File.Exists(path))
            return Fail(console, $"There is no event log at {path}.", ExitCodes.Failure);
        var log = new SqliteEventLog(path);
        var sessions = all ? log.ListSessions(cancellationToken) : [session!];
        if (!all && log.GetHead(session!, cancellationToken) == 0)
            return Fail(console, $"Session '{session}' has no events.", ExitCodes.Failure);

        // The trace names the CLI's own version as its producer, as it did before the export moved to the host.
        var export = TraceExport.Options(options, retention == "metadata" ? BeaconRetention.Metadata : BeaconRetention.Full,
            arguments.Flag("--include-local-memory"), TraceExport.VersionOf(typeof(TracesCommand).Assembly));

        var outPath = arguments.Option("--out") is { } file ? CorvidPaths.ExpandHome(file) : null;
        Stream output;
        try
        {
            output = outPath is null ? context.Streams?.Output ?? Console.OpenStandardOutput() : TraceExport.CreateOwnerOnly(outPath);
        }
        catch (IOException exception)
        {
            return Fail(console, File.Exists(outPath) ? $"{outPath} exists; give a new file." : exception.Message, ExitCodes.Failure);
        }
        int count;
        try
        {
            count = await TraceExport.WriteAsync(log, sessions, export, output, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (outPath is not null)
                await output.DisposeAsync().ConfigureAwait(false);
        }
        if (outPath is not null)
            console.MarkupLine($"Wrote {count} event(s) from {sessions.Count} session(s) to {Markup.Escape(outPath)}.");
        return ExitCodes.Success;
    }

    private static int Fail(IAnsiConsole console, string message, int code)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return code;
    }
}
