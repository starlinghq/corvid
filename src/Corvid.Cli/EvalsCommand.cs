using System.Diagnostics;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid evals ...</c> runs C-021's <c>corvid-evals</c> executable (evals/Corvid.Evals.Calibrate) by path, the way
/// Corvid starts <c>corvid-sandbox</c>: eval code is not production code, so the CLI does not reference it (architecture.md,
/// dependency rules). <c>--log</c> defaults to this user's event log.
/// </summary>
internal static class EvalsCommand
{
    public const string Variable = "CORVID_EVALS";

    private static readonly HashSet<string> TakesLog = new(StringComparer.Ordinal)
    {
        "calibrate", "report", "replay", "export-decisions", "export-trajectories",
    };

    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var tool = Locate();
        if (tool is null)
        {
            console.MarkupLine("[red]corvid evals needs the corvid-evals tool, which was not found.[/]");
            console.MarkupLine($"Build it ([bold]dotnet build evals/Corvid.Evals.Calibrate -c Release[/]) and set [bold]{Variable}[/] to the " +
                "corvid-evals executable, or put it next to corvid or on PATH.");
            return ExitCodes.Failure;
        }

        var forwarded = arguments.Arguments.ToList();
        if (forwarded.Count > 0 && TakesLog.Contains(forwarded[0]) && !forwarded.Contains("--log"))
            forwarded.AddRange(["--log", CorvidPaths.EventLog(ModelCommand.Options(host))]);

        var start = new ProcessStartInfo(tool) { UseShellExecute = false };
        foreach (var argument in forwarded)
            start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        // Through the console, so the output lands in the same scrollback (and in a test's console).
        var output = Pump(process.StandardOutput, line => console.WriteLine(line));
        var error = Pump(process.StandardError, line => console.WriteLine(line));
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string> write)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            write(line);
    }

    internal static string? Locate()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured)
            return File.Exists(configured) ? configured : null;
        var beside = Path.Combine(AppContext.BaseDirectory, "corvid-evals");
        return File.Exists(beside) ? beside : Doctor.FindOnPath("corvid-evals");
    }
}
