using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Memory;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

public sealed class MemoryToolsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly SqliteEventLog _log;
    private readonly MemoryTools _tools;
    private readonly FactGraph _facts;

    public MemoryToolsTests()
    {
        var database = new MemoryDatabase(_tree.Path("memory.db"));
        _log = new SqliteEventLog(_tree.Path("events.db"));
        _facts = new FactGraph(database);
        var retriever = new HybridRetriever(new EpisodicIndex(database), _facts);
        var writer = new MemoryWriter(database, _facts, new CoreBlockStore(database), retriever, new FakeExtractor(("person", "Anna")),
            DegradedFabric(_log), _log);
        _tools = new MemoryTools(writer, retriever);
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Risk_metadata_marks_writes_and_the_destructive_reject()
    {
        var risks = _tools.CreateTools().ToDictionary(tool => tool.Name, tool => tool.Descriptor.Risk);

        Assert.True(risks["memory_search"].ReadOnly);
        Assert.True(risks["memory_pending"].ReadOnly);
        Assert.Equal(new ToolRisk(SideEffects.Memory, false, false), risks["memory_add"]);
        Assert.Equal(new ToolRisk(SideEffects.Memory, false, false), risks["memory_approve"]);
        Assert.Equal(new ToolRisk(SideEffects.Memory, false, true), risks["memory_reject"]);
    }

    [Fact]
    public async Task An_add_is_staged_without_a_calibrated_decider_and_saved_only_on_approval()
    {
        var context = new ToolCallContext(Session, "c-1");

        var added = await Invoke(_tools, "memory_add", """{"fact":"My sister is called Anna."}""", context);

        Assert.Equal("pending_review", added.GetProperty("status").GetString());
        var pendingId = added.GetProperty("pending_id").GetString()!;
        Assert.Empty(_facts.Search("Anna"));
        // The gate's decision and the staging are in the calling session's log.
        var events = _log.Read(Session, cancellationToken: Ct).Select(e => e.Payload).ToList();
        Assert.Equal("memory_op", Assert.Single(events.OfType<Decision>()).QuestionId);
        Assert.Equal(new MemoryWritten(pendingId, MemoryOperations.Staged), Assert.Single(events.OfType<MemoryWritten>()));

        var pending = await Invoke(_tools, "memory_pending", "{}");
        var item = Assert.Single(pending.GetProperty("pending").EnumerateArray());
        Assert.Equal(pendingId, item.GetProperty("pending_id").GetString());
        Assert.Equal("My sister is called Anna.", item.GetProperty("text").GetString());

        var approved = await Invoke(_tools, "memory_approve", $$"""{"pending_id":"{{pendingId}}","operation":"add"}""", context);
        Assert.Equal("saved", approved.GetProperty("status").GetString());
        Assert.Single(_facts.Search("Anna"));
        Assert.Empty((await Invoke(_tools, "memory_pending", "{}")).GetProperty("pending").EnumerateArray());

        var found = await Invoke(_tools, "memory_search", """{"query":"sister Anna"}""");
        var result = Assert.Single(found.GetProperty("results").EnumerateArray());
        Assert.Equal("fact", result.GetProperty("kind").GetString());
        Assert.Equal("My sister is called Anna.", result.GetProperty("text").GetString());
    }

    [Fact]
    public async Task With_kev_an_add_is_saved_but_one_asked_for_in_a_tainted_session_waits_for_review()
    {
        // §10.3: text fetched from outside can ask the generator to remember things; Kev's confident add must not save it.
        var database = new MemoryDatabase(_tree.Path("kev-memory.db"));
        var facts = new FactGraph(database);
        var retriever = new HybridRetriever(new EpisodicIndex(database), facts);
        var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new UnusedEmbedder(), new UnusedChatClient()), _log,
            new ConfidentAdd());
        var tools = new MemoryTools(new MemoryWriter(database, facts, new CoreBlockStore(database), retriever,
            new FakeExtractor(("person", "Anna")), fabric, _log), retriever);

        var tainted = await Invoke(tools, "memory_add", """{"fact":"Anna's bank is Example Bank."}""",
            new ToolCallContext(Session, "c-1") { Tainted = true });
        var trusted = await Invoke(tools, "memory_add", """{"fact":"My sister is called Anna."}""", new ToolCallContext(Session, "c-2"));

        Assert.Equal("pending_review", tainted.GetProperty("status").GetString());
        Assert.Equal(MemoryWriter.TaintedReason, tainted.GetProperty("reason").GetString());
        Assert.Equal("saved", trusted.GetProperty("status").GetString());
        Assert.Equal("My sister is called Anna.", Assert.Single(facts.Search("Anna")).Fact.Text);

        // Nor can the generator approve it while the session is tainted, or without a turn's context.
        var pendingId = tainted.GetProperty("pending_id").GetString()!;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(tools, "memory_approve",
            $$"""{"pending_id":"{{pendingId}}"}""", new ToolCallContext(Session, "c-3") { Tainted = true }));
        Assert.Contains("/memory approve", refused.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(tools, "memory_approve", $$"""{"pending_id":"{{pendingId}}"}"""));
        Assert.Single(facts.Search("Anna"));
        var approved = await Invoke(tools, "memory_approve", $$"""{"pending_id":"{{pendingId}}"}""", new ToolCallContext(Session, "c-4"));
        Assert.Equal("saved", approved.GetProperty("status").GetString());
    }

    private sealed class ConfidentAdd : IDecider
    {
        public string Name => "kev-test";

        public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, request.Questions.ToImmutableDictionary(pair => pair.Key,
                pair => DecisionAnswer.For(pair.Value, pair.Value.Options.Select(option => option.Name == "add" ? 0.97 : 0.01)),
                StringComparer.Ordinal)));
    }

    [Fact]
    public async Task An_add_holding_a_credential_is_refused_with_a_reason_that_never_holds_the_value()
    {
        const string token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8"; // fictitious, in GitHub's classic shape

        var added = await Invoke(_tools, "memory_add", $$"""{"fact":"The deploy token is {{token}}."}""", new ToolCallContext(Session, "c-1"));

        Assert.Equal("refused", added.GetProperty("status").GetString());
        Assert.Equal("holds a credential value (github-token); keep secrets in Corvid Vault", added.GetProperty("reason").GetString());
        Assert.False(added.TryGetProperty("pending_id", out _));
        Assert.Empty((await Invoke(_tools, "memory_pending", "{}")).GetProperty("pending").EnumerateArray());
        Assert.Empty(_log.Read(Session, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_rejected_write_is_never_saved()
    {
        var added = await Invoke(_tools, "memory_add", """{"fact":"The car is red."}""", new ToolCallContext(Session, "c-1"));
        var pendingId = added.GetProperty("pending_id").GetString()!;

        var rejected = await Invoke(_tools, "memory_reject", $$"""{"pending_id":"{{pendingId}}","reason":"wrong car"}""");

        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Empty(_facts.Search("car"));
        var context = new ToolCallContext(Session, "c-2");
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "memory_approve", $$"""{"pending_id":"{{pendingId}}"}""", context));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "memory_reject", """{"pending_id":"pending:nope"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() =>
            Invoke(_tools, "memory_approve", $$"""{"pending_id":"{{pendingId}}","operation":"delete"}""", context));
    }

    [Fact]
    public async Task Adding_needs_the_turns_session()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(_tools, "memory_add", """{"fact":"x"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "memory_search", """{"query":" "}"""));
    }
}

public sealed class CronToolsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly JobStore _store;
    private readonly CronTools _tools;

    public CronToolsTests()
    {
        _store = new JobStore(_tree.Path("jobs.db"), TimeZoneInfo.Utc);
        _tools = new CronTools(_store);
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Creating_and_removing_are_schedule_side_effects()
    {
        var risks = _tools.CreateTools().ToDictionary(tool => tool.Name, tool => tool.Descriptor.Risk);

        Assert.Equal(new ToolRisk(SideEffects.Schedule, false, false), risks["cron_create"]);
        Assert.Equal(new ToolRisk(SideEffects.Schedule, false, true), risks["cron_remove"]);
        Assert.True(risks["cron_list"].ReadOnly);
    }

    [Fact]
    public async Task Jobs_are_created_listed_and_removed()
    {
        var context = new ToolCallContext(Session, "c-1") { Tainted = true };

        var created = await Invoke(_tools, "cron_create", """{"schedule":"weekdays at 9am","prompt":"Summarize my notes for the day"}""", context);

        var jobId = created.GetProperty("job_id").GetString()!;
        Assert.Equal("Summarize my notes for the day", created.GetProperty("name").GetString());
        Assert.True(created.GetProperty("recurring").GetBoolean());
        Assert.EndsWith("T09:00+00:00", created.GetProperty("next_run").GetString(), StringComparison.Ordinal);
        var job = _store.Get(jobId)!;
        Assert.Equal(Session, job.OriginSession);
        Assert.True(job.CreatedTainted);
        Assert.Equal("cron 0 9 * * 1-5", job.Spec);

        var check = await Invoke(_tools, "cron_create",
            """{"schedule":"every 30m","name":"disk","pre_run_script":"df -h .","no_agent":true}""");
        Assert.True(check.GetProperty("no_agent").GetBoolean());
        Assert.Equal("df -h .", check.GetProperty("pre_run_script").GetString());

        var listed = await Invoke(_tools, "cron_list", "{}");
        Assert.Equal([jobId, check.GetProperty("job_id").GetString()],
            listed.GetProperty("jobs").EnumerateArray().Select(item => item.GetProperty("job_id").GetString()));

        await Invoke(_tools, "cron_remove", $$"""{"job_id":"{{jobId}}"}""");
        Assert.Single((await Invoke(_tools, "cron_list", "{}")).GetProperty("jobs").EnumerateArray());
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "cron_remove", $$"""{"job_id":"{{jobId}}"}"""));
    }

    [Fact]
    public async Task A_paused_job_is_listed_as_paused_without_a_next_run()
    {
        var job = _store.Add(new NewJob("inbox", "check the inbox", "every 30m"));
        _store.Pause(job.JobId);

        var listed = Assert.Single((await Invoke(_tools, "cron_list", "{}")).GetProperty("jobs").EnumerateArray());
        Assert.True(listed.GetProperty("paused").GetBoolean());
        Assert.Equal("active", listed.GetProperty("state").GetString());
        Assert.False(listed.TryGetProperty("next_run", out _));

        _store.Resume(job.JobId);
        listed = Assert.Single((await Invoke(_tools, "cron_list", "{}")).GetProperty("jobs").EnumerateArray());
        Assert.False(listed.TryGetProperty("paused", out _));
        Assert.True(listed.TryGetProperty("next_run", out _));
    }

    [Theory]
    [InlineData("""{"schedule":"sometime soon","prompt":"x"}""", "schedule")]
    [InlineData("""{"schedule":"every 5s","prompt":"x"}""", "schedule")]
    [InlineData("""{"schedule":"in 5m"}""", "prompt")]
    [InlineData("""{"schedule":"in 5m","no_agent":true}""", "pre_run_script")]
    public async Task Invalid_jobs_are_refused_so_the_model_can_repair_them(string arguments, string parameter)
    {
        var error = await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "cron_create", arguments));

        Assert.Equal(parameter, error.ParamName);
        Assert.Empty(_store.List(includeFinished: true));
    }
}

public sealed class BuiltinToolsTests
{
    [Fact]
    public void Every_built_in_tool_has_example_requests_and_the_shell_mapping_names_its_command()
    {
        using var tree = new TempTree();
        using var web = new WebFetchTool(new CannedHandler(_ => CannedHandler.Page("")));
        var log = new SqliteEventLog(tree.Path("events.db"));
        var database = new MemoryDatabase(tree.Path("memory.db"));
        var facts = new FactGraph(database);
        var retriever = new HybridRetriever(new EpisodicIndex(database), facts);
        var registry = new ToolRegistry();
        registry.Add(new FileTools(new Workspace(tree.Root)));
        registry.Add(new ShellTool(new SandboxedShell(new RecordingSandbox(), new ShellOptions { Workspace = tree.Root })));
        registry.Add(web);
        registry.Add(new MemoryTools(new MemoryWriter(database, facts, new CoreBlockStore(database), retriever, new FakeExtractor(), DegradedFabric(log), log),
            retriever));
        registry.Add(new CronTools(new JobStore(tree.Path("jobs.db"))));

        var names = registry.List().Select(tool => tool.Name).ToList();
        Assert.Equal(15, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), BuiltinTools.Examples.Keys.Order(StringComparer.Ordinal));
        Assert.All(BuiltinTools.Examples.Values, examples => Assert.InRange(examples.Length, 5, 10));
        Assert.Equal(ShellTool.CommandParameter, BuiltinTools.ShellToolMapping[ShellTool.ToolName]);
        Assert.All(registry.List(), tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
    }
}
