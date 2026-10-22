using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Memory;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

/// <summary>
/// The S2 tool-calling golden set (C-034) offers the built-in tools from a snapshot in
/// <c>evals/tool-calling/catalogue.json</c>, so the eval needs no workspace, sandbox, memory or job store. This test keeps
/// the snapshot equal to the generated descriptors and <see cref="BuiltinTools.Examples"/>.
/// </summary>
public sealed class CatalogueSnapshotTests : IDisposable
{
    private const string Fix =
        "Record the snapshot with CORVID_RECORD_CATALOGUE=1 tests/Corvid.Tools.Builtin.Tests/bin/Release/net10.0/Corvid.Tools.Builtin.Tests " +
        "-class \"*CatalogueSnapshotTests\", review the diff, and re-check the cases in evals/tool-calling/cases.jsonl that use the tool " +
        "(their argument constraints are written against these schemas).";

    private static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static string CatalogueFile => Path.Combine(AppContext.BaseDirectory, "tool-calling", "catalogue.json");

    private static string SourceCatalogueFile([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "evals", "tool-calling", "catalogue.json"));

    /// <summary>Every built-in tool provider, constructed as the other tests here construct them; only descriptors are read.</summary>
    private IReadOnlyList<ToolFunction> BuiltinFunctions()
    {
        var workspace = _tree.Path("work");
        Directory.CreateDirectory(workspace);
        var database = new MemoryDatabase(_tree.Path("memory.db"));
        var log = new SqliteEventLog(_tree.Path("events.db"));
        var facts = new FactGraph(database);
        var retriever = new HybridRetriever(new EpisodicIndex(database), facts);
        var writer = new MemoryWriter(database, facts, new CoreBlockStore(database), retriever, new FakeExtractor(), DegradedFabric(log), log);
        using var web = new WebFetchTool();
        IToolProvider[] providers =
        [
            new FileTools(new Workspace(workspace)),
            new ShellTool(new SandboxedShell(new RecordingSandbox(), new ShellOptions { Workspace = workspace })),
            web,
            new MemoryTools(writer, retriever),
            new CronTools(new JobStore(_tree.Path("jobs.db"), TimeZoneInfo.Utc)),
        ];
        return [.. providers.SelectMany(provider => provider.CreateTools())];
    }

    [Fact]
    public void The_catalogue_snapshot_matches_the_generated_built_in_descriptors_and_examples()
    {
        var functions = BuiltinFunctions();
        if (Environment.GetEnvironmentVariable("CORVID_RECORD_CATALOGUE") == "1")
            Record(SourceCatalogueFile(), functions);

        using var document = JsonDocument.Parse(File.ReadAllText(CatalogueFile));
        var builtins = document.RootElement.GetProperty("tools").EnumerateArray()
            .Where(tool => tool.GetProperty("origin").GetString() == "builtin")
            .ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);

        // Every tool with shortlist examples is a built-in the golden set covers, and the snapshot has no others.
        Assert.True(BuiltinTools.Examples.Keys.Order(StringComparer.Ordinal).SequenceEqual(builtins.Keys.Order(StringComparer.Ordinal)),
            $"The snapshot's built-ins ({string.Join(", ", builtins.Keys.Order(StringComparer.Ordinal))}) differ from BuiltinTools.Examples. {Fix}");
        foreach (var function in functions.Where(function => BuiltinTools.Examples.ContainsKey(function.Name)))
        {
            var entry = builtins[function.Name];
            Assert.True(function.Description == entry.GetProperty("description").GetString(),
                $"{function.Name}: the description changed. {Fix}");
            Assert.True(JsonElement.DeepEquals(function.JsonSchema, entry.GetProperty("parameters")),
                $"{function.Name}: the JSON schema changed. Generated: {function.JsonSchema.GetRawText()} {Fix}");
            Assert.True(BuiltinTools.Examples[function.Name].SequenceEqual(entry.GetProperty("examples").EnumerateArray().Select(item => item.GetString()!)),
                $"{function.Name}: BuiltinTools.Examples changed. {Fix}");
        }
        Assert.Equal(BuiltinTools.Examples.Count, functions.Count(function => BuiltinTools.Examples.ContainsKey(function.Name)));
    }

    /// <summary>Rewrites the built-in entries of the source catalogue, first and in provider order; the MCP-like extras are kept.</summary>
    private static void Record(string path, IReadOnlyList<ToolFunction> functions)
    {
        using var existing = JsonDocument.Parse(File.ReadAllText(path));
        var root = existing.RootElement;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Indented))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject().Where(property => property.Name != "tools"))
                property.WriteTo(writer);
            writer.WriteStartArray("tools");
            foreach (var function in functions.Where(function => BuiltinTools.Examples.ContainsKey(function.Name)))
            {
                writer.WriteStartObject();
                writer.WriteString("name", function.Name);
                writer.WriteString("origin", "builtin");
                writer.WriteString("description", function.Description);
                writer.WritePropertyName("parameters");
                function.JsonSchema.WriteTo(writer);
                writer.WriteStartArray("examples");
                foreach (var example in BuiltinTools.Examples[function.Name])
                    writer.WriteStringValue(example);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            foreach (var tool in root.GetProperty("tools").EnumerateArray().Where(tool => tool.GetProperty("origin").GetString() != "builtin"))
                tool.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        File.Copy(path, CatalogueFile, overwrite: true);
    }
}
