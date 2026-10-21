using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Microsoft.Extensions.AI;

namespace S3.Gliner2Targets;

/// <summary>One target the structural analyzer found, as written in the command (the span GLiNER2 should find).</summary>
/// <param name="Kind"><c>path</c>, <c>pid</c>, <c>host</c>, <c>url</c> or <c>process_name</c> (not scored, see the write-up).</param>
/// <param name="Value">The analyzer's resolved value: the normalized path, the PID, the bare host name, the URL.</param>
/// <param name="Scored">False for an audited over-approximation (<see cref="Corpus.Overreach"/>): neither a miss nor a false positive.</param>
public sealed record GoldTarget(string Kind, string Written, string Value, bool Scored = true);

public sealed record CorpusRow(int Id, string Source, string Split, string Command, ImmutableArray<GoldTarget> Targets, int ImplicitTargets);

public sealed record DroppedRow(string Source, string Command, string Reason);

/// <summary>
/// Builds the S3 corpus: commands seeded from tests/Corvid.Approvals.Tests plus everyday commands, labelled by the
/// structural analyzer (never by hand), keeping only commands it parses exactly.
/// </summary>
public static partial class Corpus
{
    public const int Size = 300;

    // The environment the approval tests use, so the bypass shapes parse as they do there.
    public static ApprovalEnvironment Environment { get; } =
        ApprovalEnvironment.Create("/home/user/work/project", "/home/user", runtimeDirectory: "/run/user/1000");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    // Candidates in this order, kept until the corpus has Size commands: every bypass and launcher shape first, then the
    // everyday commands (which carry most hosts and PIDs), then the remaining test files.
    private static readonly string[] SeedFiles = ["BypassTests.cs", "LauncherTests.cs", "everyday.txt", "AnalyzerTests.cs",
        "PipelineTests.cs", "HardlineTests.cs", "ParserTests.cs"];

    public static string Folder(string repo) => Path.Combine(repo, "evals", "spikes", "S3-gliner2-targets");

    public static int Build(string repo)
    {
        var candidates = new List<(string Source, string Command)>();
        var seedCount = 0;
        foreach (var file in SeedFiles)
        {
            if (file == "everyday.txt")
            {
                foreach (var line in File.ReadLines(Path.Combine(Folder(repo), "everyday.txt")))
                    if (line.Trim() is { Length: > 0 } command && !command.StartsWith('#'))
                        candidates.Add(("everyday", command));
                continue;
            }

            foreach (var (source, command) in Seeds(Path.Combine(repo, "tests", "Corvid.Approvals.Tests", file)))
            {
                candidates.Add((source, command));
                seedCount++;
            }
        }

        using var db = new TempDb();
        var pipeline = new ApprovalPipeline(Environment,
            new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new NoEmbedder(), new NoChat()), db.Log), db.Log);

        var kept = new List<(string Source, string Command, ImmutableArray<GoldTarget> Targets, int Implicit)>();
        var dropped = new List<DroppedRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unused = 0;
        foreach (var (source, command) in candidates)
        {
            if (kept.Count >= Size)
            {
                unused++;
                continue;
            }

            if (!seen.Add(command))
            {
                dropped.Add(new(source, command, "duplicate"));
                continue;
            }

            var (found, implicitCount, reason) = Label(pipeline.Analyze(new ShellAction(command)), command);
            if (reason is not null)
                dropped.Add(new(source, command, reason));
            else
                kept.Add((source, command, found, implicitCount));
        }

        if (kept.Count < Size)
            throw new InvalidOperationException($"Only {kept.Count} commands kept; add everyday commands.");

        // Stratified halves: group by which target kinds a command has, order each group by a hash of the text, alternate.
        var split = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in kept.GroupBy(row => string.Join(',', row.Targets.Select(t => t.Kind).Where(k => k != "process_name").Distinct().Order())))
        {
            var index = 0;
            foreach (var row in group.OrderBy(row => Hash(row.Command), StringComparer.Ordinal))
                split[row.Command] = index++ % 2 == 0 ? "tune" : "report";
        }

        var fixtures = Path.Combine(Folder(repo), "fixtures");
        Directory.CreateDirectory(fixtures);
        File.WriteAllLines(Path.Combine(fixtures, "targets-corpus.jsonl"), kept.Select((row, i) =>
            JsonSerializer.Serialize(new CorpusRow(i + 1, row.Source, split[row.Command], row.Command, row.Targets, row.Implicit), Json)));
        File.WriteAllLines(Path.Combine(fixtures, "targets-dropped.jsonl"), dropped.Select(row => JsonSerializer.Serialize(row, Json)));

        var targets = kept.SelectMany(row => row.Targets).ToList();
        Console.WriteLine($"candidates: {candidates.Count} ({seedCount} from tests, {candidates.Count - seedCount} everyday); not needed after {Size} kept: {unused}");
        Console.WriteLine($"kept: {kept.Count} (" + string.Join(", ", kept.GroupBy(r => r.Source).Select(g => $"{g.Key} {g.Count()}")) + ")");
        Console.WriteLine($"dropped: {dropped.Count}");
        foreach (var group in dropped.GroupBy(row => Category(row.Reason)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {group.Count(),4}  {group.Key}  ({string.Join(", ", group.GroupBy(r => r.Source).Select(g => $"{g.Key} {g.Count()}"))})");
        Console.WriteLine($"scored targets ({targets.Count(t => !t.Scored)} audited over-approximations not scored): " + string.Join(", ", targets.Where(t => t.Scored).GroupBy(t => t.Kind).Select(g => $"{g.Key} {g.Count()}")) +
            $"; implicit (no written form, not scored): {kept.Sum(r => r.Implicit)}");
        foreach (var half in new[] { "tune", "report" })
            Console.WriteLine($"  {half}: {kept.Count(r => split[r.Command] == half)} commands, " + string.Join(", ",
                kept.Where(r => split[r.Command] == half).SelectMany(r => r.Targets).Where(t => t.Scored).GroupBy(t => t.Kind).Select(g => $"{g.Key} {g.Count()}")));
        return 0;
    }

    public static string Category(string reason)
    {
        var colon = reason.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? reason : reason[..colon];
    }

    /// <summary>The analyzer's targets as gold spans, or why the command is not parsed exactly.</summary>
    public static (ImmutableArray<GoldTarget> Targets, int Implicit, string? Reason) Label(ActionAnalysis analysis, string command)
    {
        if (analysis.Unsupported is { } unsupported)
            return ([], 0, "outside the parsed subset: " + unsupported);
        if (analysis.Commands.SelectMany(c => c.Unresolved).FirstOrDefault() is { } unresolved)
            return ([], 0, "unresolved: " + unresolved);
        if (!analysis.OpaqueScripts.IsDefaultOrEmpty)
            return ([], 0, "opaque script: code the analyzer does not parse (python -c, awk, ...)");
        var targets = new List<GoldTarget>();
        var implicitCount = 0;
        foreach (var target in analysis.Targets)
        {
            if (target.Dynamic)
                return ([], 0, "dynamic target: part of a target is only known at run time");
            if (target.Kind == TargetKind.Recipient)
                continue;
            var written = SourceForm(target.Written, command);
            if (written is "")
            {
                implicitCount++;
                continue;
            }

            if (written is null)
                return ([], 0, $"not as written: the target '{target.Written}' is not literally in the command");
            var kind = target.Kind switch
            {
                TargetKind.Path => "path",
                TargetKind.Process => "pid",
                TargetKind.ProcessName => "process_name",
                TargetKind.Host => "host",
                TargetKind.Url => "url",
                _ => throw new InvalidOperationException(target.Kind.ToString()),
            };
            var gold = new GoldTarget(kind, written, target.Value, !Overreach.Contains((command, written)));
            if (!targets.Contains(gold))
                targets.Add(gold);
        }

        return ([.. targets], implicitCount, null);
    }

    // The analyzer's Written is the word after static expansion, so "~/.ssh" shows as "/home/user/.ssh". Map it back to the
    // text in the command, mechanically: the home directory as ~, $HOME or ${HOME}, the data home as $XDG_DATA_HOME.
    private static readonly (string Expanded, string[] Source)[] Expansions =
    [
        ("/home/user/.local/share", ["$XDG_DATA_HOME", "${XDG_DATA_HOME}"]),
        ("/home/user", ["~", "$HOME", "${HOME}"]),
    ];

    /// <summary>
    /// Targets the analyzer records conservatively that are not what the label names: approvals treat any operand of a
    /// network program as a host and any operand of a file program as a path, which is right for approvals and wrong as
    /// span labels. Found by reading every labelled row once; they stay in the fixture, marked, and are not scored.
    /// Nothing is ever added by hand.
    /// </summary>
    public static readonly HashSet<(string Command, string Written)> Overreach =
    [
        ("mkfs -t ext4 /dev/sdb1", "ext4"),                  // the -t option's value (a file system type)
        ("rg --pre ./decode TODO", "TODO"),                  // the search pattern
        ("ag --pager 'sh -c id' TODO", "TODO"),              // the search pattern
        ("find ~ -exec rm -rf {} +", "{}"),                  // find's placeholder for each match
        ("find / -name core -exec rm -rf {} \\;", "{}"),
        ("gdb -p 2600", "2600"),                             // a PID read as a file operand
        ("http GET https://httpbin.org/get", "GET"),         // the HTTP method
        ("dig @1.1.1.1 starling.dev MX", "MX"),              // the record type
        ("dig +short txt _dmarc.example.com", "+short"),     // a query option
        ("dig +short txt _dmarc.example.com", "txt"),        // the record type
        ("socat - TCP:localhost:6379", "-"),                 // standard input
        ("socat - TCP:localhost:6379", "TCP:localhost:6379"), // an address whose host is "localhost", recorded whole
    ];

    // Targets the analyzer derives rather than reads from a word: a program's default output, and the tree a program
    // writes into ("*" for the working directory, "dir/*" for "-C dir" or "-d dir").
    private static readonly HashSet<string> DefaultOutputs = new(StringComparer.Ordinal) { "nohup.out", "perf.data", "perf.data.old" };

    /// <summary>The target as it appears in the command; "" for a derived target with no text; null when it cannot be found.</summary>
    private static string? SourceForm(string? written, string command)
    {
        // An option's value is recorded with its option word ("--output=log.txt"); the target is the value.
        if (written is ['-', ..] && written.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals
            && command.Contains(written, StringComparison.Ordinal))
            return written[(equals + 1)..];
        if (string.IsNullOrEmpty(written) || DefaultOutputs.Contains(written) && !command.Contains(written, StringComparison.Ordinal))
            return "";
        if (command.Contains(written, StringComparison.Ordinal))
            return written;
        foreach (var (expanded, sources) in Expansions)
            if (written.StartsWith(expanded, StringComparison.Ordinal) && (written.Length == expanded.Length || written[expanded.Length] == '/'))
                foreach (var source in sources)
                    if (source + written[expanded.Length..] is var candidate && command.Contains(candidate, StringComparison.Ordinal))
                        return candidate;
        if (written == "*")
            return "";
        if (written.EndsWith("/*", StringComparison.Ordinal))
        {
            var directory = written[..^2];
            return command.Contains(directory, StringComparison.Ordinal) ? directory : SourceForm(directory, command) is { Length: > 0 } source ? source : null;
        }

        return null;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Command strings in a test file: every literal in a <c>TheoryData</c> block, and the first argument of
    /// <c>[InlineData(...)]</c>, <c>Analyze(...)</c> and <c>ShellAction(...)</c>. A literal without a space is a fragment
    /// (a glob, a file name), not a command line, and is skipped.
    /// </summary>
    private static IEnumerable<(string Source, string Command)> Seeds(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var text = File.ReadAllText(file);
        var found = new List<(int Position, string Source, string Command)>();
        foreach (Match block in TheoryBlock().Matches(text))
            foreach (Match literal in Literal().Matches(block.Groups["body"].Value))
                found.Add((block.Index + literal.Index, $"{name}.{block.Groups["name"].Value}", Unescape(literal.Groups["s"].Value)));
        foreach (Match call in FirstArgument().Matches(text))
            found.Add((call.Index, name, Unescape(call.Groups["s"].Value)));
        return found.OrderBy(f => f.Position).Where(f => f.Command.Contains(' ', StringComparison.Ordinal)).Select(f => (f.Source, f.Command));
    }

    private static string Unescape(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] != '\\' || i + 1 == literal.Length)
            {
                builder.Append(literal[i]);
                continue;
            }

            builder.Append(literal[++i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '0' => '\0',
                var c => c,
            });
        }

        return builder.ToString();
    }

    [GeneratedRegex("""TheoryData<string> (?<name>\w+) => new\((?<body>.*?)\);""", RegexOptions.Singleline)]
    private static partial Regex TheoryBlock();

    [GeneratedRegex(@"(?<!@|\$)""(?<s>(?:[^""\\\n]|\\.)*)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"(?:\[InlineData\(|Analyze\(|ShellAction\()""(?<s>(?:[^""\\\n]|\\.)*)""")]
    private static partial Regex FirstArgument();
}

/// <summary>An event database for the pipeline, which <see cref="ApprovalPipeline.Analyze"/> never writes to.</summary>
internal sealed class TempDb : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-s3-" + Guid.NewGuid().ToString("N"));

    public TempDb() => Log = new SqliteEventLog(Path.Combine(_root, "events.db"));

    public SqliteEventLog Log { get; }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

internal sealed class NoChat : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class NoEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
