using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary><c>corvid model</c>: list models and slots, change a slot's default, and download models explicitly.</summary>
internal static class ModelCommand
{
    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var positional = arguments.Positional();
        return positional switch
        {
            [] or ["list"] => List(host, context.Console),
            ["switch", var slot, var model] => Switch(host, context.Console, arguments, slot, model),
            ["download", .. var models] => await DownloadAsync(host, context.Console, models, arguments.Flag("--all"), cancellationToken).ConfigureAwait(false),
            _ => Usage(context.Console),
        };
    }

    private static int Usage(IAnsiConsole console)
    {
        console.MarkupLine("[red]usage: corvid model [[list]] | switch <slot> <model> | download <model>... | download --all[/]");
        return ExitCodes.Usage;
    }

    private static int List(IHost host, IAnsiConsole console)
    {
        var catalogue = host.Services.GetRequiredService<ModelCatalogue>();
        var options = Options(host);
        var slots = options.Models.EffectiveSlots();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumns("model", "slots", "profile", "size", "status", "configured as");
        foreach (var model in catalogue.Models)
        {
            var availability = catalogue.Check(model.Name);
            var roles = slots.Where(pair => string.Equals(pair.Value.Model, model.Name, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key)
                .Concat(slots.Where(pair => pair.Value.Escalation.Concat(pair.Value.Fallbacks).Contains(model.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(pair => pair.Key + " (alternative)"));
            table.AddRow(
                new Text(model.Name),
                new Text(string.Join(", ", model.Slots)),
                new Text(model.Profile.Id),
                new Text(model.DownloadBytes is { } bytes ? FormatBytes(bytes) : "-"),
                new Markup(availability.IsReady ? "[green]ready[/]" : $"[yellow]{availability.Readiness.ToString().ToLowerInvariant()}[/]"),
                new Text(string.Join(", ", roles)));
        }
        console.Write(table);
        console.MarkupLine($"[grey]Model cache: {Markup.Escape(host.Services.GetRequiredService<Corvid.Models.Starling.StarlingModelStore>().CacheDirectory)}[/]");
        // Short lines, as below: a redirected console wraps at 80 columns.
        console.MarkupLine("[grey]Without kev-0.8b the decider runs on the degraded tier.[/]");
        console.MarkupLine("[grey]kev-4b is an optional second tier for gates.[/]");
        if (catalogue.Models.Any(model => !catalogue.Check(model.Name).IsReady))
        {
            // Short lines: a redirected console wraps at 80 columns, and the command should stay whole to copy.
            console.MarkupLine("Nothing downloads implicitly. To fetch a model:");
            console.MarkupLine("  [bold]corvid model download <model>[/]   (or --all)");
        }
        return ExitCodes.Success;
    }

    private static int Switch(IHost host, IAnsiConsole console, CliArguments arguments, string slot, string model)
    {
        var catalogue = host.Services.GetRequiredService<ModelCatalogue>();
        if (!ModelSlots.All.Contains(slot, StringComparer.Ordinal))
        {
            console.MarkupLine($"[red]'{Markup.Escape(slot)}' is not a slot; the slots are {Markup.Escape(string.Join(", ", ModelSlots.All))}.[/]");
            return ExitCodes.Usage;
        }
        if (!catalogue.TryFind(model, out var descriptor))
        {
            console.MarkupLine($"[red]No model is named '{Markup.Escape(model)}'. `corvid model` lists them.[/]");
            return ExitCodes.Usage;
        }

        // The file is validated with the change applied before it is written, so a bad switch never breaks the next start.
        var file = arguments.ConfigFile ?? CorvidPaths.UserConfigFile;
        var document = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject() : new JsonObject();
        var slotNode = Ensure(Ensure(Ensure(document, CorvidOptions.Section), "Models"), "Slots");
        Ensure(slotNode, slot)["Model"] = descriptor.Name;
        // Everything this run sees (environment, --set) with the switch applied, which is what the next start will validate.
        var candidate = new ConfigurationBuilder()
            .AddConfiguration(host.Services.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{CorvidOptions.Section}:Models:Slots:{slot}:Model"] = descriptor.Name })
            .Build().GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();
        var validation = host.Services.GetRequiredService<IValidateOptions<CorvidOptions>>().Validate(null, candidate);
        if (validation.Failed)
        {
            foreach (var failure in validation.Failures ?? [])
                console.MarkupLine($"[red]•[/] {Markup.Escape(failure)}");
            return ExitCodes.Usage;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, file, overwrite: true);
        console.MarkupLine($"[green]{Markup.Escape(slot)} now defaults to {Markup.Escape(descriptor.Name)}[/] [grey]({Markup.Escape(file)}; new sessions use it)[/]");
        var availability = catalogue.Check(descriptor.Name);
        if (!availability.IsReady)
            console.MarkupLine($"[yellow]It is not in the model cache yet:[/] run [bold]{Markup.Escape(availability.Acquire ?? "corvid model download " + descriptor.Name)}[/].");
        return ExitCodes.Success;
    }

    private static async Task<int> DownloadAsync(IHost host, IAnsiConsole console, IReadOnlyList<string> names, bool all, CancellationToken cancellationToken)
    {
        var catalogue = host.Services.GetRequiredService<ModelCatalogue>();
        // --all fetches what can be fetched: remote models (C-015) have nothing to download.
        var models = all ? catalogue.Models.Where(model => model.DownloadBytes is not null).Select(model => model.Name).ToArray() : names.ToArray();
        if (models.Length == 0)
            return Usage(console);
        var failed = false;
        foreach (var name in models)
        {
            if (!catalogue.TryFind(name, out var model))
            {
                console.MarkupLine($"[red]No model is named '{Markup.Escape(name)}'.[/]");
                failed = true;
                continue;
            }
            console.MarkupLine($"Downloading [bold]{Markup.Escape(model.Name)}[/]" +
                (model.DownloadBytes is { } bytes ? $" ({FormatBytes(bytes)})" : "") + "…");
            try
            {
                var progress = new Progress<ModelDownloadProgress>();
                var lastFile = "";
                progress.ProgressChanged += (_, item) =>
                {
                    if (item.File != lastFile && item.Bytes == 0)
                        console.MarkupLine($"  [grey]{Markup.Escape(lastFile = item.File)}[/]");
                };
                var result = await catalogue.DownloadAsync(model.Name, progress, cancellationToken).ConfigureAwait(false);
                console.MarkupLine(result.IsReady ? $"[green]✓[/] {Markup.Escape(result.Detail)}" : $"[red]✗[/] {Markup.Escape(result.Detail)}");
                failed |= !result.IsReady;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                console.MarkupLine($"[red]✗ {Markup.Escape(model.Name)}: {Markup.Escape(exception.Message)}[/]");
                failed = true;
            }
        }
        return failed ? ExitCodes.Failure : ExitCodes.Success;
    }

    /// <summary>What `chat` prints when a configured model is not in the cache: each model, why, and the command that fetches it.</summary>
    public static void ReportMissing(IAnsiConsole console, ImmutableArray<ModelAvailability> missing)
    {
        // A remote model (C-015) is not downloaded: it is unavailable when Vault is not running or lacks its key.
        var cached = missing.Any(model => model.Readiness != ModelReadiness.Unavailable);
        console.MarkupLine(cached
            ? "[yellow]Corvid cannot start: a configured model is not in the model cache.[/] Nothing is downloaded implicitly."
            : "[yellow]Corvid cannot start: a configured remote model is unavailable.[/]");
        foreach (var model in missing)
        {
            console.MarkupLine($"  [yellow]•[/] {Markup.Escape(model.Detail)}");
            if (model.Acquire is { } acquire)
                console.MarkupLine($"    fetch it: [bold]{Markup.Escape(acquire)}[/]");
        }
        console.MarkupLine(cached
            ? "Run [bold]corvid doctor[/] for the full picture, or point Corvid:Models:Cache"
            : "Run [bold]corvid doctor[/] for the full picture.");
        if (cached)
            console.MarkupLine("(or STARLING_MODELS) at a model cache that has them.");
    }

    internal static CorvidOptions Options(IHost host) =>
        host.Services.GetRequiredService<IConfiguration>().GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();

    internal static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0 GiB", CultureInfo.InvariantCulture)
        : (bytes / (double)(1L << 20)).ToString("0 MiB", CultureInfo.InvariantCulture);

    private static JsonObject Ensure(JsonObject parent, string key)
    {
        // Configuration keys ignore case, so an existing "corvid" or "models" object is the one to extend.
        var existing = parent.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing.Value is JsonObject found)
            return found;
        var created = new JsonObject();
        parent[existing.Key ?? key] = created;
        return created;
    }
}
