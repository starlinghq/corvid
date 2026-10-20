using Corvid.Host.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary><c>corvid doctor</c>: every check, what is wrong and how to fix it. Exits 1 when a check failed.</summary>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(arguments.Flag("--thorough"), cancellationToken).ConfigureAwait(false);
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey).AddColumns("", "area", "check", "detail");
        foreach (var check in checks)
            table.AddRow(new Markup(Mark(check.Status)), new Text(check.Area), new Text(check.Name), new Text(check.Detail));
        console.Write(table);
        // Fixes below the table, one per line, so a command is never wrapped inside a cell and can be copied whole.
        var fixes = checks.Where(check => check.Fix is not null && check.Status is CheckStatus.Failed or CheckStatus.Warning).ToList();
        if (fixes.Count > 0)
        {
            console.MarkupLine("[bold]To fix:[/]");
            foreach (var check in fixes)
                console.MarkupLine($"  {Mark(check.Status)} {Markup.Escape(check.Name)}: {Markup.Escape(check.Fix!)}");
        }
        var failed = checks.Count(check => check.Status == CheckStatus.Failed);
        var warnings = checks.Count(check => check.Status == CheckStatus.Warning);
        console.MarkupLine(failed > 0
            ? $"[red]{failed} check(s) failed[/], {warnings} warning(s)."
            : warnings > 0 ? $"[yellow]{warnings} warning(s)[/], nothing failed." : "[green]Everything checked is in place.[/]");
        if (!arguments.Flag("--thorough"))
            console.MarkupLine("[grey]Model files were checked by size; --thorough hashes them (about a second per 500 MB).[/]");
        return failed > 0 ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static string Mark(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[green]✓[/]",
        CheckStatus.Info => "[blue]i[/]",
        CheckStatus.Warning => "[yellow]![/]",
        _ => "[red]✗[/]",
    };
}
