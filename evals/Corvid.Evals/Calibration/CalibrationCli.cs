using System.Globalization;
using Corvid.Fabric;

namespace Corvid.Evals.Calibration;

/// <summary>
/// The command line behind <c>corvid-evals</c> (evals/Corvid.Evals.Calibrate), which becomes <c>corvid evals calibrate</c>
/// with the CLI (C-013). Kept in the library so tests drive it directly. Exit codes: 0 success, 1 failure, 2 usage.
/// </summary>
public static class CalibrationCli
{
    public const string Usage = """
        usage: corvid-evals <command> --log <events.db> [--log <events.db> ...] [options]

        commands:
          calibrate            fit floors and write a new versioned floors file
                                 --version <v> (required)  --out <floors.json> (required)
                                 [--floors <current.json>] [--report-json <file>]
                                 [--gate-precision 0.9] [--routing-precision 0.8] [--min-support 20]
          report               reliability bins, acted and escalation rates, coverage    [--json <file>]
          replay               decisions a candidate floors file would change            --candidate <floors.json> [--json <file>]
          export-decisions     Kev training records, JSON lines                          --out <file> [--labelled-only] [--calibrated-only]
          export-trajectories  ShareGPT trajectories, JSON lines                         --out <file>

        common options:
          --positive <a,b>     extra outcome names that count as positive
          --negative <a,b>     extra outcome names that count as negative

        Only calibrated decisions fit floors. Degraded-tier decisions are reported separately and never fitted.
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--labelled-only", "--calibrated-only" };

    private static readonly HashSet<string> Valued = new(StringComparer.Ordinal)
    {
        "--log", "--version", "--out", "--floors", "--report-json", "--gate-precision", "--routing-precision", "--min-support",
        "--json", "--candidate", "--positive", "--negative",
    };

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            (args.Count == 0 ? error : output).WriteLine(Usage);
            return args.Count == 0 ? 2 : 0;
        }

        Options options;
        try
        {
            options = Options.Parse(args.Skip(1));
        }
        catch (UsageException exception)
        {
            error.WriteLine("corvid-evals: " + exception.Message);
            error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "calibrate" => Calibrate(options, output),
                "report" => Report(options, output),
                "replay" => Replay(options, output),
                "export-decisions" => ExportDecisions(options, output),
                "export-trajectories" => ExportTrajectories(options, output),
                _ => throw new UsageException($"unknown command '{args[0]}'"),
            };
        }
        catch (UsageException exception)
        {
            error.WriteLine("corvid-evals: " + exception.Message);
            error.WriteLine(Usage);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            error.WriteLine("corvid-evals: " + exception.Message);
            return 1;
        }
    }

    private static int Calibrate(Options options, TextWriter output)
    {
        var version = options.Required("--version");
        var target = options.Required("--out");
        var current = options.Single("--floors") is { } path ? FloorConfiguration.Parse(File.ReadAllText(path)) : FloorConfiguration.Default;
        var fitOptions = new FitOptions(version,
            options.Number("--gate-precision") ?? 0.9,
            options.Number("--routing-precision") ?? 0.8,
            (int)(options.Number("--min-support") ?? 20));
        var decisions = Load(options);
        var fit = FloorFitter.Fit(decisions, current, fitOptions);
        var report = CalibrationReport.Build(decisions, fit);
        WriteFile(target, fit.Floors.ToJson());
        if (options.Single("--report-json") is { } json)
            WriteFile(json, report.ToJson());
        output.Write(report.ToText());
        output.WriteLine($"Wrote floors {fit.Floors.Version} to {Path.GetFullPath(target)}.");
        return 0;
    }

    private static int Report(Options options, TextWriter output)
    {
        options.Forbid("--version", "--out", "--candidate");
        var report = CalibrationReport.Build(Load(options));
        if (options.Single("--json") is { } json)
            WriteFile(json, report.ToJson());
        output.Write(report.ToText());
        return 0;
    }

    private static int Replay(Options options, TextWriter output)
    {
        var candidate = FloorConfiguration.Parse(File.ReadAllText(options.Required("--candidate")));
        var result = FloorReplay.Run(Load(options), candidate);
        if (options.Single("--json") is { } json)
            WriteFile(json, result.ToJson());
        output.Write(result.ToText());
        return 0;
    }

    private static int ExportDecisions(Options options, TextWriter output)
    {
        var target = options.Required("--out");
        var decisions = Load(options);
        using var writer = new StreamWriter(target, append: false);
        var summary = KevTrainingExport.Write(decisions, writer,
            new KevExportOptions(options.Has("--labelled-only"), options.Has("--calibrated-only")));
        output.WriteLine($"Wrote {summary.Written} Kev training record(s) to {Path.GetFullPath(target)}; skipped {summary.Skipped}; {summary.MissingState} without a turn state.");
        return 0;
    }

    private static int ExportTrajectories(Options options, TextWriter output)
    {
        var target = options.Required("--out");
        var decisions = Load(options);
        using var writer = new StreamWriter(target, append: false);
        var summary = ShareGptExport.Write(decisions, writer);
        output.WriteLine($"Wrote {summary.Written} trajectory(ies) to {Path.GetFullPath(target)}; skipped {summary.Skipped} session(s) with no conversation.");
        return 0;
    }

    private static DecisionSet Load(Options options)
    {
        var logs = options.All("--log");
        if (logs.Count == 0)
            throw new UsageException("at least one --log is required");
        var classifier = OutcomeClassifier.Default;
        var positive = options.All("--positive").SelectMany(Split).ToArray();
        var negative = options.All("--negative").SelectMany(Split).ToArray();
        if (positive.Length > 0 || negative.Length > 0)
            classifier = new OutcomeClassifier(classifier.Positive.Union(positive), classifier.Negative.Union(negative));
        return DecisionSet.Load(logs, classifier: classifier);
    }

    private static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void WriteFile(string path, string content)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content.EndsWith('\n') ? content : content + "\n");
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public static Options Parse(IEnumerable<string> args)
        {
            var options = new Options();
            using var items = args.GetEnumerator();
            while (items.MoveNext())
            {
                var name = items.Current;
                if (!name.StartsWith("--", StringComparison.Ordinal))
                    throw new UsageException($"unexpected argument '{name}'");
                if (Flags.Contains(name))
                {
                    options._flags.Add(name);
                    continue;
                }
                if (!Valued.Contains(name))
                    throw new UsageException($"unknown option '{name}'");
                if (!items.MoveNext())
                    throw new UsageException($"{name} needs a value");
                if (!options._values.TryGetValue(name, out var list))
                    options._values[name] = list = [];
                list.Add(items.Current);
            }
            return options;
        }

        public bool Has(string flag) => _flags.Contains(flag);

        public IReadOnlyList<string> All(string name) => _values.TryGetValue(name, out var list) ? list : [];

        public string? Single(string name) => All(name) switch
        {
            [] => null,
            [var value] => value,
            _ => throw new UsageException($"{name} given more than once"),
        };

        public string Required(string name) => Single(name) ?? throw new UsageException($"{name} is required");

        public double? Number(string name) =>
            Single(name) is not { } text ? null
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value
            : throw new UsageException($"{name} must be a number");

        public void Forbid(params string[] names)
        {
            foreach (var name in names.Where(_values.ContainsKey))
                throw new UsageException($"{name} does not apply to this command");
        }
    }
}
