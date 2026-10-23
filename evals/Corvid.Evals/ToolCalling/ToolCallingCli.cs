using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using Corvid.Abstractions.Fabric;
using Corvid.Models;

namespace Corvid.Evals.ToolCalling;

/// <summary>
/// The models a tool-calling run binds, loaded on first use and never downloaded. The library loads nothing itself:
/// <c>corvid-evals</c> supplies Starling's CPU bindings, the S2 GPU wrapper its CUDA ones, tests fakes.
/// </summary>
public interface IToolCallingModels : IDisposable
{
    /// <summary>The generator as the host binds it (request scope, token counter).</summary>
    /// <exception cref="ToolCallingModelMissingException">The model is not in the local cache.</exception>
    ModelBinding Generator();

    /// <exception cref="ToolCallingModelMissingException">The model is not in the local cache.</exception>
    ModelBinding Embedder();

    /// <summary>GLiNER2, or null when it is not present (it is optional in every configuration).</summary>
    ModelBinding? Extractor();

    /// <summary>Kev-0.8B, the calibrated decider of configuration (c).</summary>
    /// <exception cref="ToolCallingModelMissingException">The model is not in the local cache.</exception>
    IDecider Kev();

    /// <summary>What was loaded, for the report header: model name to profile, device and package.</summary>
    IReadOnlyDictionary<string, string> Describe();
}

/// <summary>A model the run needs is not in the local cache. The run never downloads; the message names the command that does.</summary>
public sealed class ToolCallingModelMissingException(string model, string? detail = null, Exception? inner = null)
    : Exception($"{model} is not present: corvid model download {model}" + (detail is null ? "" : $" ({detail})"), inner)
{
    public string Model { get; } = model;
}

/// <summary>
/// <c>corvid-evals tool-calling</c> (C-034): runs the S2 golden set through <see cref="Corvid.Kernel.Turns.TurnRunner"/> in each configuration
/// and writes <c>report.json</c>, <c>report.md</c> and <c>events-&lt;config&gt;.db</c> to <c>--out</c>. Exit codes as
/// <c>CalibrationCli</c>: 0 success, 1 failure (a missing model, a bad case file), 2 usage.
/// </summary>
public static class ToolCallingCli
{
    public const string Usage = """
        usage: corvid-evals tool-calling --out <dir> [options]

        Runs the S2 tool-calling golden set (evals/tool-calling) through the turn loop in each configuration, on locally
        downloaded models (never downloads), and writes report.json, report.md and events-<config>.db to --out.

          --set <dir>          directory holding catalogue.json and cases.jsonl   [evals/tool-calling, else next to the binary]
          --catalogue <file>   catalogue.json, overriding --set
          --cases <file>       cases.jsonl, overriding --set
          --configs <a,b>      qwen-all, shortlist, shortlist-kev, and the opt-in     [qwen-all,shortlist,shortlist-kev]
                               shortlist-kev-no-desc (no C-033 description check), degraded and
                               shortlist-kev-transcript (S4: Kev's state holds every event)
          --tags <a,b>         only cases with any of these tags
          --limit <n>          only the first n cases (after --tags)
          --no-extractor       leave GLiNER2 out of every configuration
          --commit <sha>       the Corvid commit for the header                     [$CORVID_COMMIT]
        """;

    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "--set", "--catalogue", "--cases", "--configs", "--tags", "--limit", "--out", "--commit",
    };

    /// <summary>The untimed, unscored warm-up case each configuration runs first (S5 warmed up on a prompt outside its set).</summary>
    public static ToolCallingCase WarmUp { get; } = new("warmup", "What's the weather like in Paris today?", ["direct"],
        [new ExpectedStep(new Expectation(["weather__get_weather"], false), ImmutableSortedDictionary<string, ArgumentConstraint>.Empty)],
        false, ImmutableSortedDictionary<string, string>.Empty, null);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error, Func<IToolCallingModels> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(models);
        if (args.Count > 0 && args[0] is "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }

        Dictionary<string, string> options;
        HashSet<string> flags;
        ImmutableArray<ToolCallingConfiguration> configurations;
        ImmutableArray<string> tags;
        int? limit;
        string outputDirectory;
        try
        {
            (options, flags) = Parse(args);
            outputDirectory = options.GetValueOrDefault("--out") ?? throw new UsageException("--out is required");
            configurations = options.TryGetValue("--configs", out var names)
                ? [.. Split(names).Select(name => ToolCallingConfigurations.TryParse(name, out var configuration) ? configuration
                    : throw new UsageException($"unknown configuration '{name}'"))]
                : ToolCallingConfigurations.Default;
            if (configurations.IsEmpty)
                throw new UsageException("--configs names no configuration");
            tags = options.TryGetValue("--tags", out var tagList) ? [.. Split(tagList)] : [];
            foreach (var tag in tags.Where(tag => !ToolCallingSet.KnownTags.Contains(tag)))
                throw new UsageException($"unknown tag '{tag}'");
            limit = options.TryGetValue("--limit", out var text)
                ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new UsageException("--limit takes a positive number")
                : null;
        }
        catch (UsageException exception)
        {
            error.WriteLine("corvid-evals tool-calling: " + exception.Message);
            error.WriteLine(Usage);
            return 2;
        }

        ToolCallingSet set;
        try
        {
            var directory = options.GetValueOrDefault("--set") ?? DefaultSetDirectory();
            set = ToolCallingSet.Load(options.GetValueOrDefault("--catalogue") ?? Path.Combine(directory, "catalogue.json"),
                options.GetValueOrDefault("--cases") ?? Path.Combine(directory, "cases.jsonl"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine("corvid-evals tool-calling: " + exception.Message);
            return 1;
        }
        var cases = set.Cases.Where(item => tags.IsEmpty || item.Tags.Any(tags.Contains)).ToList();
        if (limit is { } count)
            cases = [.. cases.Take(count)];
        if (cases.Count == 0)
        {
            error.WriteLine("corvid-evals tool-calling: no case matches --tags");
            return 1;
        }

        using var loaded = models();
        ToolCallingBindings bindings;
        try
        {
            var generator = loaded.Generator();
            var embedder = configurations.Any(ToolCallingConfigurations.NeedsEmbedder) ? loaded.Embedder() : null;
            var kev = configurations.Any(ToolCallingConfigurations.NeedsKev) ? loaded.Kev() : null;
            var extractor = flags.Contains("--no-extractor") ? null : loaded.Extractor();
            bindings = new ToolCallingBindings(generator, embedder, extractor, kev);
        }
        catch (ToolCallingModelMissingException exception)
        {
            error.WriteLine("corvid-evals tool-calling: " + exception.Message);
            return 1;
        }

        var started = DateTimeOffset.UtcNow;
        var loadBefore = LoadAverage();
        var runner = new ToolCallingRunner(set, bindings, configurations, outputDirectory);
        var total = cases.Count * runner.Configurations.Length;
        output.WriteLine($"Running {cases.Count} case(s) × {runner.Configurations.Length} configuration(s) of set {set.Version}; warm-up first.");
        var scores = new List<CaseScore>();
        var kevOutcomes = "appended at the end of the run";
        // Rewritten after every case, so a run that dies part-way still leaves a report of what it scored.
        string WriteReport()
        {
            var described = loaded.Describe().ToImmutableSortedDictionary(StringComparer.Ordinal).SetItem("generator profile", bindings.Generator.Profile.Id);
            var header = new ToolCallingHeader(set.Version, set.CatalogueSha256, set.CasesSha256, set.Catalogue.Length, ToolCallingRunner.FloorsVersion,
                options.GetValueOrDefault("--commit") ?? Environment.GetEnvironmentVariable("CORVID_COMMIT") ?? "unknown",
                described,
                new Dictionary<string, string>
                {
                    ["configurations"] = string.Join(", ", runner.Configurations.Select(ToolCallingConfigurations.Name)),
                    ["cases"] = string.Create(CultureInfo.InvariantCulture, $"{scores.Count / runner.Configurations.Length} scored of {cases.Count} selected of {set.Cases.Length}") +
                        (tags.IsEmpty ? "" : $", tags {string.Join(",", tags)}") + (limit is null ? "" : string.Create(CultureInfo.InvariantCulture, $", limit {limit}")),
                    ["extractor"] = bindings.Extractor is null ? "off" : bindings.Extractor.Profile.Id,
                    ["turn options"] = "production defaults (MaxOutputTokens 1024, approval mode smart), MaxSteps = expected steps + 1",
                    ["state budget"] = string.Create(CultureInfo.InvariantCulture,
                        $"production (600 tokens, 8 recent events, 300 at a tool-and-skill ingress); shortlist-kev-transcript: {ToolCallingRunner.TranscriptStateBudget.Total} tokens (recent {ToolCallingRunner.TranscriptStateBudget.Recent}, hard stop {ToolCallingRunner.TranscriptStateBudget.HardStop}), {ToolCallingRunner.TranscriptRecentEvents} recent events, no dual-shortlist budget"),
                    ["history"] = "a history case's entries are seeded into its fresh session before the turn; only the turn's own events are scored",
                    ["shortlist"] = string.Create(CultureInfo.InvariantCulture, $"qwen-all: {set.Catalogue.Length} (every tool, no embedder); others: 5, built-in examples"),
                    ["escalation"] = "generator slot escalates to a stub that answers with fixed text",
                    ["warm-up"] = "one untimed case per configuration: " + WarmUp.Prompt,
                    ["kev outcomes appended"] = kevOutcomes,
                }.ToImmutableSortedDictionary(StringComparer.Ordinal),
                new Dictionary<string, string>
                {
                    ["load average before"] = loadBefore,
                    ["load average after"] = LoadAverage(),
                    ["processors"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
                    ["os"] = RuntimeInformation.OSDescription,
                    ["runtime"] = RuntimeInformation.FrameworkDescription,
                    ["CUDA_VISIBLE_DEVICES"] = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") ?? "(unset)",
                }.ToImmutableSortedDictionary(StringComparer.Ordinal),
                started, DateTimeOffset.UtcNow);
            var report = ToolCallingReport.Build(header, scores);
            File.WriteAllText(Path.Combine(outputDirectory, "report.json"), report.ToJson());
            var text = report.ToMarkdown();
            File.WriteAllText(Path.Combine(outputDirectory, "report.md"), text);
            return text;
        }

        var runs = await runner.RunAsync(cases, WarmUp, run =>
        {
            var score = ToolCallingScorer.Score(run, set);
            scores.Add(score);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{scores.Count}/{total}] {score.Id} {score.Configuration}: {ToolCallingScorer.Name(score.Outcome)}{(score.ArgsMatch == false ? " (args miss)" : "")} {score.TurnMs:0} ms"));
            WriteReport();
        }, cancellationToken).ConfigureAwait(false);

        var kevConfigurations = runner.Configurations.Where(ToolCallingConfigurations.NeedsKev).ToList();
        kevOutcomes = kevConfigurations.Count == 0
            ? "none (no Kev configuration ran)"
            : string.Join("; ", kevConfigurations.Select(configuration => ToolCallingConfigurations.Name(configuration) + ": "
                + ToolCallingReport.AppendKevOutcomes(runner.Log(configuration), runs.Where(run => run.Configuration == configuration))));
        var markdown = WriteReport();
        output.WriteLine();
        output.WriteLine(markdown[..Math.Max(0, markdown.IndexOf("## By tag", StringComparison.Ordinal))].TrimEnd());
        output.WriteLine();
        output.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outputDirectory, "report.json"))} and report.md.");
        return 0;
    }

    private static string DefaultSetDirectory()
    {
        var repository = Path.Combine(Environment.CurrentDirectory, "evals", "tool-calling");
        return Directory.Exists(repository) ? repository : Path.Combine(AppContext.BaseDirectory, "tool-calling");
    }

    private static string LoadAverage()
    {
        try
        {
            return File.Exists("/proc/loadavg") ? string.Join(" ", File.ReadAllText("/proc/loadavg").Split(' ').Take(3)) : "n/a";
        }
        catch (IOException)
        {
            return "n/a";
        }
    }

    private static IEnumerable<string> Split(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (Dictionary<string, string> Options, HashSet<string> Flags) Parse(IReadOnlyList<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--no-extractor")
            {
                flags.Add(name);
                continue;
            }
            if (!Valued.Contains(name))
                throw new UsageException($"unknown option '{name}'");
            if (i + 1 >= args.Count)
                throw new UsageException($"{name} needs a value");
            if (!options.TryAdd(name, args[++i]))
                throw new UsageException($"{name} is given twice");
        }
        return (options, flags);
    }

    private sealed class UsageException(string message) : Exception(message);
}
