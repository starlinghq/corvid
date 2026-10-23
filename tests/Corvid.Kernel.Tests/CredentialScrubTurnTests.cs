using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Microsoft.Data.Sqlite;

namespace Corvid.Kernel.Tests;

/// <summary>
/// C-040 end to end: a credential a tool returns never reaches events.db or memory.db, on disk, in the database or its WAL;
/// the model sees the marker; execution and checkpoints still use the real value.
/// </summary>
public sealed class CredentialScrubTurnTests : IDisposable
{
    private const string Session = "s-scrub";
    private const string Token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8"; // fictitious, in GitHub's classic shape
    private const string Marker = "ghp_[REDACTED:github-token:";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-scrub-turn-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static TestTool ReadPage() => TestTool.Create("read_page", "Read a page of the project notes.",
        new ToolRisk(SideEffects.None, Network: false, Destructive: false),
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
        (_, _) => ValueTask.FromResult<object?>($"deploy notes: export GH_TOKEN={Token}"), new ToolParameter("path", null, true, false));

    [Fact]
    public async Task A_token_in_a_tool_result_is_in_neither_events_db_nor_memory_db_and_the_model_sees_the_marker()
    {
        var eventsPath = Path.Combine(_root, "events.db");
        var log = new SqliteEventLog(eventsPath);
        var database = new MemoryDatabase(Path.Combine(_root, "memory.db"));
        var facts = new FactGraph(database);
        var episodes = new EpisodicIndex(database);
        var readPage = ReadPage();
        var generator = new FakeGenerator(Reply.Call("read_page", """{"path":"deploy"}"""), Reply.Say("Read it."));
        var embedder = new KeywordEmbedder(TurnHarness.Keywords);
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, new ModelBinding(TurnHarness.Profile("gen", ToolCallSupport.Native), generator))
            .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
        var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, generator), log);
        var tools = new ToolRegistry();
        tools.Add(readPage);
        var services = new TurnServices
        {
            Log = log,
            Slots = new SessionModelSlots(registry, log, Session),
            Fabric = fabric,
            Tools = tools,
            Options = new TurnOptions { ToolFilter = tool => tool.Name == "read_page" },
        };

        var result = await new TurnRunner(Session, services).RunAsync(new Envelope(Session, "cli", "read the deploy page"), Ct);

        Assert.Equal("Read it.", result.Text);
        Assert.Equal(1, readPage.Invocations);
        // The generator's second round is rendered from the log: the marker, never the token.
        var secondRound = generator.Calls[1].AllText;
        Assert.DoesNotContain(Token, secondRound, StringComparison.Ordinal);
        Assert.Contains("export GH_TOKEN=" + Marker, secondRound, StringComparison.Ordinal);
        // Kev's view: every decision's state and question, including those after the tool ran, holds no token.
        var decisions = log.Read(Session, limit: 4096, cancellationToken: Ct).Select(item => item.Payload).OfType<Decision>().ToList();
        Assert.NotEmpty(decisions);
        Assert.All(decisions, decision => Assert.DoesNotContain(Token, EventJson.Serialize(decision), StringComparison.Ordinal));

        // The episodic index is derived from the scrubbed log.
        var indexed = episodes.CatchUp(log, Session, Ct);
        Assert.Contains(indexed, episode => episode.Text.Contains(Marker, StringComparison.Ordinal));
        Assert.DoesNotContain(indexed, episode => episode.Text.Contains(Token, StringComparison.Ordinal));

        // A memory write holding the same token is refused before the gate or the pending store sees it.
        var writer = new MemoryWriter(database, facts, new CoreBlockStore(database), new HybridRetriever(episodes, facts),
            new FakeExtractor(0.9f), fabric, log);
        var refused = await writer.WriteAsync(new MemoryWriteRequest(Session, $"The deploy token is {Token}"), Ct);
        Assert.Equal(MemoryWriteStatus.Refused, refused.Status);
        Assert.DoesNotContain(Token, refused.Reason!, StringComparison.Ordinal);

        // On disk: checkpoint both WALs into their databases, drop pooled connections, then read every file's bytes.
        Checkpoint(eventsPath);
        Checkpoint(database.Path);
        SqliteConnection.ClearAllPools();
        var token = Encoding.UTF8.GetBytes(Token);
        var marker = Encoding.UTF8.GetBytes(Marker);
        foreach (var path in new[] { eventsPath, eventsPath + "-wal", database.Path, database.Path + "-wal" }.Where(File.Exists))
            Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(token) < 0, $"the token is in {Path.GetFileName(path)}");
        Assert.True(File.ReadAllBytes(eventsPath).AsSpan().IndexOf(marker) >= 0, "the marker is not in events.db");
        Assert.True(File.ReadAllBytes(database.Path).AsSpan().IndexOf(marker) >= 0, "the marker is not in memory.db");
    }

    private static void Checkpoint(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task A_write_with_a_token_in_its_content_runs_with_the_value_checkpoints_and_undoes()
    {
        using var fixture = new CheckpointFixture(options => options with { MaxFileBytes = 64 * 1024 });
        fixture.Write("deploy.txt", "OLD=1\n");
        var content = $"GH_TOKEN={Token}\n";
        using var harness = new TurnHarness(
            new FakeGenerator(Reply.Call("write_file", JsonSerializer.Serialize(new { path = "deploy.txt", content })), Reply.Say("Saved.")),
            prompter: FakePrompter.Always(approve: true), workspace: fixture.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" },
            approvalOptions: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping },
            tools: tools => tools.Add(new FileTools(new Workspace(fixture.Workspace))))
        {
            Checkpoints = fixture.Service,
        };

        await harness.RunAsync("put the token in deploy.txt");

        // The tool ran with the in-memory arguments, so the file holds the real value, by design.
        Assert.Equal(content, fixture.Read("deploy.txt"));
        var events = harness.Events();
        var call = Assert.IsType<ToolCallIssued>(Assert.Single(events, item => item.Payload is ToolCallIssued).Payload);
        Assert.StartsWith("GH_TOKEN=" + Marker, call.Arguments.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.All(events, item => Assert.DoesNotContain(Token, EventJson.Serialize(item.Payload), StringComparison.Ordinal));
        var checkpoints = events.Select(item => item.Payload).OfType<CheckpointTaken>().ToList();
        Assert.Equal([CheckpointKinds.Before, CheckpointKinds.After], checkpoints.Select(checkpoint => checkpoint.Kind));

        var snapshot = SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: Ct);
        var undone = await fixture.Service.RestoreAsync(harness.Log, TurnHarness.Session, CheckpointService.PlanUndo(snapshot)!, Ct);

        Assert.Equal(RestoreStatus.Restored, undone.Status);
        Assert.Equal("OLD=1\n", fixture.Read("deploy.txt"));
    }
}
