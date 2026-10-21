// Spike S1 (C-003): checkpoint latency of Kev-0.8B and Kev-4B through Corvid's real path. See docs/spikes/S1-kev-latency.md.
//
// One process per (model, device). Every cell builds its TurnState with TurnStateBuilder and the Qwen3.5 token counter
// (what TurnRunner uses with a Starling generator), asks the §5.3 catalogue questions through DecisionFabric with the
// KevDecider adapter as the calibrated tier and a SqliteEventLog, and times each checkpoint after warm-up.
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Models.Starling;
using Starlings.Models.Kev;
using Starlings.Runtime.Onnx;

var arguments = Args.Parse(args);
var kevModel = arguments.Get("model", "0.8b") switch
{
    "0.8b" => KevModel.Kev08B,
    "4b" => KevModel.Kev4B,
    var other => throw new ArgumentException($"--model {other}: expected 0.8b or 4b"),
};
var device = arguments.Get("device", "cpu");
if (device is not ("cpu" or "cuda"))
    throw new ArgumentException($"--device {device}: expected cpu or cuda");
var calls = int.Parse(arguments.Get("calls", "50"), CultureInfo.InvariantCulture);
var warmup = int.Parse(arguments.Get("warmup", "5"), CultureInfo.InvariantCulture);
var cellFilter = arguments.Get("cells", "all");
var outPath = arguments.Get("out", $"s1-{arguments.Get("model", "0.8b")}-{device}.jsonl");
var tokenizerPath = arguments.Get("qwen-tokenizer", "");
if (tokenizerPath.Length == 0)
    tokenizerPath = Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache/starlings/models/Qwen/Qwen3.5-0.8B"), "tokenizer.json", SearchOption.AllDirectories).Single();

var counter = Qwen35TokenCounter.FromFile(tokenizerPath);
Log($"Qwen3.5 token counter: {tokenizerPath}");

// Locate the cached bundle offline (size check, as Corvid's StarlingModelStore opens it), then load.
var openWatch = Stopwatch.StartNew();
var files = KevModelAssets.OpenLocal(kevModel, new Starlings.Acquisition.ModelAcquisitionOptions
{
    Verification = Starlings.Acquisition.CacheVerification.Size,
});
openWatch.Stop();
Log($"bundle: {files.ModelPath} (opened in {openWatch.ElapsedMilliseconds} ms)");

var kevOptions = device == "cuda"
    ? new KevOptions { Session = new OnnxSessionOptions { Provider = OnnxExecutionProvider.Cuda, CudaDeviceId = 0 } }
    : null;
var loadWatch = Stopwatch.StartNew();
var model = KevDecisionModel.Create(files, kevOptions);
loadWatch.Stop();
Log($"loaded {model.ProfileId} in {loadWatch.Elapsed.TotalSeconds:F2} s; serialized native calls: {model.SerializesNativeCalls}");

// KevDecider.Create is CPU-only (C-004b deferred CUDA placement), so the spike constructs the adapter through its internal
// constructor with a runner over the same KevDecisionModel. Create does exactly this for the CPU (ModelRunner), minus the
// provider guard; the Decide logic is the adapter's own code.
var runner = new TimedRunner(model);
using var kev = new KevDecider(KevDecider.NameOf(kevModel), runner, model);
var timedKev = new TimedDecider(kev);

var root = Path.Combine(Path.GetTempPath(), "corvid-s1-" + Guid.NewGuid().ToString("N"));
var log = new SqliteEventLog(Path.Combine(root, "events.db"));
var fabric = new DecisionFabric(DecisionPolicy.Default, new UnusedDegraded(), log, timedKev);
var catalogue = fabric.Policy.Catalogue;

var header = new Dictionary<string, object?>
{
    ["kind"] = "load",
    ["model"] = KevDecider.NameOf(kevModel),
    ["profile"] = model.ProfileId,
    ["device"] = device,
    ["load_s"] = Math.Round(loadWatch.Elapsed.TotalSeconds, 3),
    ["open_ms"] = openWatch.ElapsedMilliseconds,
    ["processors"] = Environment.ProcessorCount,
    ["cuda_visible_devices"] = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES"),
    ["malloc_mmap_threshold"] = Environment.GetEnvironmentVariable("MALLOC_MMAP_THRESHOLD_"),
    ["started"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
};
Write(outPath, header);

// Cells: ingress at 300/600/1,500 tokens x 4/8 questions, the one-question pre-execute risk checkpoint, then a row sweep.
var cells = new List<Cell>();
foreach (var size in new[] { 300, 600, 1500 })
{
    cells.Add(new Cell($"ingress-{size}-q4", size, Checkpoint.Ingress, () => Ingress(4)));
    cells.Add(new Cell($"ingress-{size}-q8", size, Checkpoint.Ingress, () => Ingress(8)));
}
cells.Add(new Cell("pre-execute-600-risk", 600, Checkpoint.PreExecute, () => [Risk(0)]));
foreach (var row in new[] { 250, 500, 750, 1000 })
    cells.Add(new Cell($"rows-600-risk-{row}", 600, Checkpoint.PreExecute, () => [Risk(row)]));

var selected = cellFilter == "all"
    ? cells
    : cells.Where(cell => cellFilter.Split(',').Any(prefix => cell.Name.StartsWith(prefix, StringComparison.Ordinal))).ToList();

var session = "s1-" + Guid.NewGuid().ToString("N")[..8];
foreach (var cell in selected)
{
    var states = Variants.Select(variant => BuildState(variant, cell.StateTokens)).ToArray();
    foreach (var state in states)
        if (state.ExceedsHardStop)
            throw new InvalidOperationException($"{cell.Name}: state over the hard stop ({state.Tokens}); the fabric would skip Kev.");
    var questions = cell.Questions();

    var fabricMs = new List<double>();
    var kevMs = new List<double>();
    var encodeMs = new List<double>();
    var runMs = new List<double>();
    var rowTokens = new List<int>();
    var stateIds = new List<int>();
    var loadBefore = LoadAverage();
    var cellWatch = Stopwatch.StartNew();
    for (var i = 0; i < warmup + calls; i++)
    {
        var state = states[i % states.Length];
        runner.Reset();
        timedKev.Reset();
        var watch = Stopwatch.StartNew();
        var result = await fabric.DecideAsync(session, cell.Checkpoint, state, questions);
        watch.Stop();
        // Every question must have been answered by Kev itself: a fallback or a declined row would time nothing.
        if (result.Fallback is not null || result.Verdicts.Values.Any(v => v.Answer is null || v.Decider != kev.Name))
            throw new InvalidOperationException($"{cell.Name}: not answered by {kev.Name}: {result.Decider}; " +
                string.Join(", ", result.Verdicts.Select(pair => $"{pair.Key}={pair.Value.Decider}")));
        if (i < warmup)
            continue;
        fabricMs.Add(watch.Elapsed.TotalMilliseconds);
        kevMs.Add(timedKev.LastMs);
        encodeMs.Add(runner.EncodeMs);
        runMs.Add(runner.RunMs);
        stateIds.Add(runner.StateIds);
        rowTokens.AddRange(runner.RowTokens);
    }
    cellWatch.Stop();

    var record = new Dictionary<string, object?>
    {
        ["kind"] = "cell",
        ["model"] = kev.Name,
        ["device"] = device,
        ["cell"] = cell.Name,
        ["checkpoint"] = cell.Checkpoint.ToString(),
        ["questions"] = questions.Count,
        ["question_keys"] = questions.Select(q => q.Key).ToArray(),
        ["target_state_tokens"] = cell.StateTokens,
        ["state_tokens_corvid"] = states.Select(s => s.Tokens).ToArray(),
        ["state_truncated"] = states.Select(s => string.Join('+', s.Truncated)).Distinct().ToArray(),
        ["state_ids_kev"] = new[] { stateIds.Min(), stateIds.Max() },
        ["row_tokens_kev"] = new[] { rowTokens.Min(), rowTokens.Max() },
        ["row_tokens_mean"] = Math.Round(rowTokens.Average(), 1),
        ["warmup"] = warmup,
        ["calls"] = fabricMs.Count,
        ["fabric_ms"] = Stats.Of(fabricMs),
        ["kev_decider_ms"] = Stats.Of(kevMs),
        ["kev_encode_ms"] = Stats.Of(encodeMs),
        ["kev_run_ms"] = Stats.Of(runMs),
        ["wall_s"] = Math.Round(cellWatch.Elapsed.TotalSeconds, 1),
        // Other jobs share this workstation; the 1-minute load average before and after the cell says how busy it was.
        ["loadavg_before"] = loadBefore,
        ["loadavg_after"] = LoadAverage(),
    };
    Write(outPath, record);
    var f = Stats.Of(fabricMs);
    Log($"{kev.Name} {device} {cell.Name}: state {string.Join('/', states.Select(s => s.Tokens).Distinct())} tok, rows {rowTokens.Min()}-{rowTokens.Max()}, " +
        $"p50 {f["p50"]} p90 {f["p90"]} p99 {f["p99"]} mean {f["mean"]} ms (n={fabricMs.Count}, load {loadBefore}->{LoadAverage()})");
}

try { Directory.Delete(root, recursive: true); } catch (IOException) { }
return 0;

// ---- inputs -----------------------------------------------------------------------------------------------------------

List<KeyValuePair<string, DecisionQuestion>> Ingress(int count)
{
    // TurnRunner's ingress order (intent, difficulty, clarify, needs_recall, tool, skill), plus injection and delegate for 8.
    var list = new List<KeyValuePair<string, DecisionQuestion>>
    {
        Ask("intent"), Ask("difficulty"), Ask("clarify"), Ask("needs_recall"),
    };
    if (count == 8)
    {
        list.Add(KeyValuePair.Create("tool", catalogue["tool"].ToQuestion(
            [.. Tools.Select(t => new QuestionOption(t.Name, t.Description)), new QuestionOption("none", "no tool is needed")])));
        list.Add(KeyValuePair.Create("skill", catalogue["skill"].ToQuestion(
            [.. Skills.Select(s => new QuestionOption(s.Name, s.Description)), new QuestionOption("none", "no saved skill applies")])));
        list.Add(Ask("injection"));
        list.Add(Ask("delegate"));
    }
    return list;
}

KeyValuePair<string, DecisionQuestion> Ask(string id) => KeyValuePair.Create(id, catalogue[id].ToQuestion());

// The risk question as ApprovalPipeline asks it: instructions are the RiskDocument JSON (question plus the analysed action,
// snake_case). ActionDocuments is internal to Corvid.Approvals, so the same shape is written here. padTo > 0 grows the
// command text until Kev's row for the question is as close to padTo tokens as fits.
KeyValuePair<string, DecisionQuestion> Risk(int padTo)
{
    var entry = catalogue["risk"];
    DecisionQuestion Make(int extraArgs)
    {
        var extra = string.Concat(Enumerable.Range(0, extraArgs).Select(i => $" src/module{i}/generated/output{i}.bin"));
        var command = "rm -rf build/ dist/" + extra + " && git push --force origin main";
        var doc = new
        {
            question = entry.Instructions,
            action = new
            {
                kind = "shell",
                text = command,
                unsupported = (string?)null,
                tainted = false,
                reversible = (bool?)false,
                commands = new object[]
                {
                    new { verb = "rm", subcommand = (string?)null, effects = new[] { "delete" }, via = Array.Empty<string>(), substitution = false,
                          unresolved = Array.Empty<string>(),
                          targets = new[] { "build/", "dist/" }.Concat(Enumerable.Range(0, extraArgs).Select(i => $"src/module{i}/generated/output{i}.bin"))
                              .Select(p => new { kind = "path", value = p, access = "write", location = "workspace", recursive = true, pattern = false, dynamic = false }).ToArray() },
                    new { verb = "git", subcommand = (string?)"push", effects = new[] { "network", "remote_write" }, via = Array.Empty<string>(), substitution = false,
                          unresolved = Array.Empty<string>(),
                          targets = new[] { new { kind = "remote", value = "origin main", access = "write", location = "remote", recursive = false, pattern = false, dynamic = false } } },
                },
            },
        };
        var json = JsonSerializer.SerializeToElement(doc);
        return entry.ToQuestion(instructions: DecisionContent.FromJson(json));
    }

    if (padTo <= 0)
        return KeyValuePair.Create("risk", Make(0));
    var probeState = BuildState(Variants[0], 600);
    int RowOf(DecisionQuestion q)
    {
        var request = new KevRequest(KevContent.Json(probeState.Json), [KeyValuePair.Create("risk", KevDecider.ToKev(q)!)]);
        return model.Encode(request.ToRecord()).Rows[0].Ids.Length;
    }
    int low = 0, high = 64;
    while (RowOf(Make(high)) <= padTo)
        high *= 2;
    while (low < high)
    {
        var mid = (low + high + 1) / 2;
        if (RowOf(Make(mid)) <= padTo)
            low = mid;
        else
            high = mid - 1;
    }
    return KeyValuePair.Create("risk", Make(low));
}

TurnState BuildState(Variant variant, int target)
{
    var budget = target switch
    {
        300 => new TurnStateBudget(Total: 300),
        600 => TurnStateBudget.Default,
        1500 => new TurnStateBudget(Total: 1500, HardStop: 1500, UserMessage: 700, Recent: 420, Goal: 80, Entities: 120, LastToolResult: 300),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
    var builder = new TurnStateBuilder(counter, budget);
    return builder.Build(new TurnStateInput(variant.UserMessage, "cli", "qwen3.5-0.8b local, 8k", variant.Recent, variant.Goal,
        variant.Entities, Step: 0, LastToolResult: variant.LastToolResult));
}

static void Write(string path, Dictionary<string, object?> record)
{
    File.AppendAllText(path, JsonSerializer.Serialize(record) + "\n");
}

static string LoadAverage()
{
    try { return File.ReadAllText("/proc/loadavg").Split(' ')[0]; }
    catch (IOException) { return "?"; }
}

static void Log(string message) => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

// ---- fixed content ----------------------------------------------------------------------------------------------------

partial class Program
{
    internal static readonly (string Name, string Description)[] Tools =
    [
        ("shell", "run a shell command in the workspace sandbox"),
        ("read_file", "read a text file from the workspace"),
        ("write_file", "create or overwrite a file in the workspace"),
        ("search_files", "search file contents with a regular expression"),
        ("web_fetch", "fetch a web page and return its text"),
        ("memory_search", "search remembered facts from earlier conversations"),
        ("calendar_list", "list upcoming calendar events"),
        ("send_message", "send a message to a contact on a connected gateway"),
    ];

    internal static readonly (string Name, string Description)[] Skills =
    [
        ("release-notes", "draft release notes from merged pull requests since the last tag"),
        ("triage-ci-failure", "find the failing step of a CI run, its log and the likely cause"),
        ("weekly-review", "summarise the week's commits, issues and calendar into a short review"),
        ("dependency-bump", "update a package version, rebuild and run the tests"),
    ];

    internal static readonly Variant[] Variants = [MakeVariant(0), MakeVariant(1), MakeVariant(2)];

    private static Variant MakeVariant(int seed)
    {
        string[] asks =
        [
            "The nightly build of the corvid repository failed again on the integration tests. Look at the last CI run, find which test failed and why, and if it is the flaky approval test, rerun only that job.",
            "Can you go through the logs from yesterday's deploy and tell me whether the database migration finished before the service restarted, and whether any requests failed in that window?",
            "Draft a short summary of what changed in the memory writer this week, then check whether the docs still describe the staging flow correctly, and open an issue for anything that is out of date.",
        ];
        var ask = asks[seed];
        // A pasted log follows the request, as users do; the builder cuts it to the user_message budget.
        var pasted = new StringBuilder(ask).Append("\n\nHere is what I saw:\n");
        for (var i = 0; i < 120; i++)
            pasted.Append(CultureInfo.InvariantCulture,
                $"2026-10-2{seed} 03:{i % 60:00}:{(i * 7) % 60:00} INFO worker[{i % 6}] step {i}: processed batch {1000 + i * 13} in {40 + (i * 17) % 300} ms, queue depth {i % 9}\n");
        var recent = Enumerable.Range(0, 24).Select(i => (i % 3) switch
        {
            0 => $"user asked about the {(i % 2 == 0 ? "build" : "deploy")} status of job {200 + i} and the agent answered from memory",
            1 => $"tool shell ran `git log --oneline -n {5 + i}` and returned {5 + i} lines; exit code 0",
            _ => $"decision intent=use_tools (confidence 0.{70 + i}) at ingress; tool shortlist of 8 offered",
        }).ToImmutableArray();
        var entities = ImmutableSortedDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            KeyValuePair.Create("path", ImmutableArray.Create("src/Corvid.Memory/MemoryWriter.cs", "docs/architecture.md", ".github/workflows/ci.yml", "tests/Corvid.Approvals.Tests")),
            KeyValuePair.Create("project", ImmutableArray.Create("corvid", "starling", "updraft")),
            KeyValuePair.Create("person", ImmutableArray.Create("Mark", "Kev")),
            KeyValuePair.Create("date", ImmutableArray.Create("yesterday", "this week", "2026-10-20")),
        });
        var toolResult = new StringBuilder("exit code 1\n");
        for (var i = 0; i < 60; i++)
            toolResult.Append(CultureInfo.InvariantCulture, $"  at Corvid.Kernel.Turns.TurnRunner.RunAsync() step {i} failed assertion: expected {i} decisions but found {i + 1}\n");
        return new Variant(pasted.ToString(), recent,
            "Find why the nightly integration tests fail, fix or rerun the flaky job, and report back to the user with the cause and the run link.",
            entities, toolResult.ToString());
    }
}

internal sealed record Variant(string UserMessage, ImmutableArray<string> Recent, string Goal,
    ImmutableSortedDictionary<string, ImmutableArray<string>> Entities, string LastToolResult);

internal sealed record Cell(string Name, int StateTokens, Checkpoint Checkpoint, Func<List<KeyValuePair<string, DecisionQuestion>>> Questions);

/// <summary>The adapter's runner (Kev's two stages), timed, recording the state and row sizes Kev actually ran.</summary>
internal sealed class TimedRunner(KevDecisionModel model) : IKevRunner
{
    public double EncodeMs { get; private set; }
    public double RunMs { get; private set; }
    public int StateIds { get; private set; }
    public List<int> RowTokens { get; } = [];

    public void Reset()
    {
        EncodeMs = 0;
        RunMs = 0;
        StateIds = 0;
        RowTokens.Clear();
    }

    public KevEncoding Encode(KevRecord record)
    {
        var watch = Stopwatch.StartNew();
        var encoding = model.Encode(record);
        EncodeMs += watch.Elapsed.TotalMilliseconds;
        return encoding;
    }

    public IReadOnlyList<float[]> Run(KevEncoding encoding, CancellationToken cancellationToken)
    {
        StateIds = encoding.StateIds.Length;
        RowTokens.AddRange(encoding.Rows.Select(row => row.Ids.Length));
        var watch = Stopwatch.StartNew();
        var result = model.Run(encoding, cancellationToken);
        RunMs += watch.Elapsed.TotalMilliseconds;
        return result;
    }
}

/// <summary>Times the calibrated tier as the fabric calls it.</summary>
internal sealed class TimedDecider(IDecider inner) : IDecider
{
    public double LastMs { get; private set; }

    public string Name => inner.Name;

    public void Reset() => LastMs = 0;

    public async ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var result = await inner.DecideAsync(request, cancellationToken);
        LastMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }
}

/// <summary>The degraded tier is never reached in this spike; reaching it is a failed measurement.</summary>
internal sealed class UnusedDegraded : IDecider
{
    public string Name => "degraded";

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The degraded tier was asked; Kev did not answer.");
}

internal static class Stats
{
    /// <summary>Nearest-rank percentiles, mean, min and max in milliseconds (one decimal).</summary>
    public static Dictionary<string, double> Of(List<double> values)
    {
        var sorted = values.Order().ToArray();
        double Rank(double p) => sorted[Math.Clamp((int)Math.Ceiling(p / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new Dictionary<string, double>
        {
            ["p50"] = Math.Round(Rank(50), 1),
            ["p90"] = Math.Round(Rank(90), 1),
            ["p99"] = Math.Round(Rank(99), 1),
            ["mean"] = Math.Round(sorted.Average(), 1),
            ["min"] = Math.Round(sorted[0], 1),
            ["max"] = Math.Round(sorted[^1], 1),
        };
    }
}

internal sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Args Parse(string[] args)
    {
        var parsed = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"unexpected argument '{args[i]}'; use --name value");
            parsed._values[args[i][2..]] = args[++i];
        }
        return parsed;
    }

    public string Get(string name, string fallback) => _values.GetValueOrDefault(name, fallback);
}
