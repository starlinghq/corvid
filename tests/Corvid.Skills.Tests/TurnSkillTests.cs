using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Kernel.Tests;
using Corvid.Kernel.Turns;
using Corvid.Skills;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Skills.Tests;

/// <summary>An in-memory skill source for the turn loop.</summary>
internal sealed class FakeSkills(params SkillBody[] skills) : ISkillSource
{
    public List<string> Loaded { get; } = [];

    public IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools) =>
        [.. skills.Select(skill => new SkillOption(skill.Name, skill.Text.Split('\n')[0], [], skill.Trusted))];

    public SkillBody? Load(string name)
    {
        Loaded.Add(name);
        return skills.FirstOrDefault(skill => skill.Name == name);
    }
}

internal sealed class RecordingLearning : ISkillLearning
{
    public List<SkillOpportunity> Queued { get; } = [];

    public void Queue(SkillOpportunity opportunity) => Queued.Add(opportunity);
}

public sealed class TurnSkillTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TurnRunner Runner(TurnHarness harness, ISkillSource? skills, ISkillLearning? learning = null) =>
        new(TurnHarness.Session, new TurnServices
        {
            Log = harness.Log,
            Slots = harness.Slots,
            Fabric = harness.Fabric,
            Tools = harness.Tools,
            Approvals = harness.Approvals,
            Options = harness.Options,
            Skills = skills,
            SkillLearning = learning,
        });

    private static SkillBody Body(string name, string text, bool trusted = true) => new(name, text, trusted, ["references/a.md"]);

    [Fact]
    public async Task AConfidentSkillPickLoadsTheBodyIntoTheSystemPrompt()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Done.")));
        var skills = new FakeSkills(Body("weather-check", "weather forecast for a city\n1. Ask for the city."), Body("note-tidy", "tidy the note list"));

        var result = await harness.RunAsync(Runner(harness, skills), "what's the weather tomorrow");

        Assert.Equal("weather-check", result.Skill);
        var decision = Assert.Single(harness.Decisions("skill"));
        Assert.Equal("load_skill_body:weather-check", decision.Action);
        Assert.Equal(["none", "note-tidy", "weather-check"], decision.Question!.Options.Select(option => option.Name).Order(StringComparer.Ordinal));
        var system = harness.Generator.Calls[0].Messages[0].Text;
        Assert.Contains("Skill weather-check: follow it for this request.", system, StringComparison.Ordinal);
        Assert.Contains("1. Ask for the city.", system, StringComparison.Ordinal);
        Assert.Contains("references/a.md", system, StringComparison.Ordinal);
        Assert.False(result.Tainted);
    }

    [Fact]
    public async Task BelowTheFloorOnlyTheIndexIsShown()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Done.")));
        var skills = new FakeSkills(Body("weather-check", "weather forecast"), Body("weather-alerts", "weather warnings"));

        // Both skills match the message equally, so the embedding margin is too small to act on.
        var result = await harness.RunAsync(Runner(harness, skills), "weather");

        Assert.Null(result.Skill);
        Assert.Equal("skill_index_only", Assert.Single(harness.Decisions("skill")).Action);
        Assert.Empty(skills.Loaded);
        var system = harness.Generator.Calls[0].Messages[0].Text;
        Assert.Contains("Saved skills that may apply (not loaded)", system, StringComparison.Ordinal);
        Assert.Contains("- weather-check: weather forecast", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfidentNoneShowsNoSkills()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Done.")));
        var skills = new FakeSkills(Body("weather-check", "weather forecast"));

        var result = await harness.RunAsync(Runner(harness, skills), "hello there");

        Assert.Null(result.Skill);
        Assert.Equal("load_skill_body:none", Assert.Single(harness.Decisions("skill")).Action);
        Assert.DoesNotContain("skill", harness.Generator.Calls[0].Messages[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHubSkillsBodyTaintsTheTurnAndIsCheckedForInjection()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Done.")));
        var skills = new FakeSkills(Body("weather-check", "weather forecast for a city", trusted: false));

        var result = await harness.RunAsync(Runner(harness, skills), "weather tomorrow please");

        Assert.Equal("weather-check", result.Skill);
        Assert.True(result.Tainted);
        Assert.Equal("treat_as_tainted", Assert.Single(harness.Decisions("injection/skill:weather-check")).Action);
    }

    [Fact]
    public async Task ATaintedSkillMakesSideEffectsAndNetworkAskEvenWhenRiskIsLow()
    {
        var prompter = FakePrompter.Always(approve: false);
        using var harness = new TurnHarness(new FakeGenerator(Reply.Call("fetch_page", """{"url":"https://example.com"}"""), Reply.Say("Done.")),
            prompter: prompter, calibrated: new RiskOnlyDecider(0),
            options: TurnOptions.Default with { ApprovalMode = ApprovalMode.Permissive, PinnedSkill = "page-reader" },
            tools: tools => tools.Add(TestTool.FetchPage()));
        var skills = new FakeSkills(Body("page-reader", "read a web page", trusted: false));

        var result = await harness.RunAsync(Runner(harness, skills), "read the page for me");

        Assert.Equal("page-reader", result.Skill);
        Assert.Single(prompter.Prompts);
    }

    [Fact]
    public async Task APinnedSkillLoadsWithoutAskingSkill()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("Done.")), options: TurnOptions.Default with { PinnedSkill = "note-tidy" });
        var skills = new FakeSkills(Body("note-tidy", "tidy the note list"));

        var result = await harness.RunAsync(Runner(harness, skills), "hello");

        Assert.Equal("note-tidy", result.Skill);
        Assert.Empty(harness.Decisions("skill"));
    }

    [Fact]
    public async Task SkillOpportunityQueuesATurnOfThreeOrMoreToolCalls()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Say("Three lists."))
        {
            YesNo = FakeGenerator.YesTo("reusable"),
        };
        using var harness = new TurnHarness(generator);
        var learning = new RecordingLearning();

        var result = await harness.RunAsync(Runner(harness, null, learning), "list my notes three times");

        Assert.True(result.SkillQueued);
        var decision = Assert.Single(harness.Decisions("skill_opportunity"));
        Assert.Equal("queue_skill_distillation", decision.Action);
        var queued = Assert.Single(learning.Queued);
        Assert.Equal(decision.DecisionId, queued.DecisionId);
        var events = harness.Events();
        Assert.IsType<UserMessage>(events.Single(item => item.Sequence == queued.FromSequence).Payload);
        Assert.IsType<AssistantMessage>(events.Single(item => item.Sequence == queued.ThroughSequence).Payload);
    }

    [Fact]
    public async Task ShortTurnsAreNotAskedAboutSkillOpportunities()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Say("One list.")) { YesNo = FakeGenerator.YesTo("reusable") };
        using var harness = new TurnHarness(generator);
        var learning = new RecordingLearning();

        var result = await harness.RunAsync(Runner(harness, null, learning), "list my notes");

        Assert.False(result.SkillQueued);
        Assert.Empty(harness.Decisions("skill_opportunity"));
        Assert.Empty(learning.Queued);
    }

    [Fact]
    public async Task AToolCanMarkItsResultAsOutsideContent()
    {
        var tool = TestTool.Create("read_hub_file", "Read a hub file.", new ToolRisk(SideEffects.None, false, false), """{"type":"object","properties":{}}""",
            (arguments, _) =>
            {
                ToolCallContext.From(arguments)!.MarkResultTainted();
                return ValueTask.FromResult<object?>("hub text");
            });
        using var harness = new TurnHarness(new FakeGenerator(Reply.Call("read_hub_file"), Reply.Say("Read.")), tools: tools => tools.Add(tool));

        var result = await harness.RunAsync("read the hub file");

        Assert.True(result.Tainted);
    }
}

public sealed class SkillFileToolTests : IDisposable
{
    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    private async Task<SkillStore> StoreAsync()
    {
        TempRoot.Write(_root["skills/mine/SKILL.md"], TempRoot.Skill("mine", body: "My steps."));
        TempRoot.Write(_root["skills/mine/references/a.md"], "reference A");
        TempRoot.Write(_root["hub/theirs/SKILL.md"], TempRoot.Skill("theirs", body: "Their steps."));
        TempRoot.Write(_root["hub/theirs/scripts/run.sh"], "echo hi\n");
        var store = await SkillStore.OpenAsync(new SkillStoreOptions { Root = _root["skills"] }, TestContext.Current.CancellationToken);
        await store.ImportAsync(_root["hub"], SkillAuthor.Hub("test"), cancellationToken: TestContext.Current.CancellationToken);
        return store;
    }

    private static async Task<object?> CallAsync(ToolFunction tool, ToolCallContext context, string arguments) =>
        await tool.InvokeAsync(context.AttachTo(new AIFunctionArguments(
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!)), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ReadReturnsBodiesAndFilesAndTaintsForHubSkills()
    {
        var tools = new SkillFileTools(await StoreAsync()).CreateTools().ToDictionary(tool => tool.Name);
        var mine = new ToolCallContext("s", "c1");
        var theirs = new ToolCallContext("s", "c2");

        var body = (await CallAsync(tools["skill_read"], mine, """{"name":"mine"}"""))!.ToString()!;
        var file = (await CallAsync(tools["skill_read"], mine, """{"name":"mine","file":"references/a.md"}"""))!.ToString()!;
        var hub = (await CallAsync(tools["skill_read"], theirs, """{"name":"theirs"}"""))!.ToString()!;

        Assert.Contains("My steps.", body, StringComparison.Ordinal);
        Assert.Contains("reference A", file, StringComparison.Ordinal);
        Assert.False(mine.ResultTainted);
        Assert.Contains("Their steps.", hub, StringComparison.Ordinal);
        Assert.True(theirs.ResultTainted);
        await Assert.ThrowsAsync<ToolArgumentException>(() => CallAsync(tools["skill_read"], mine, """{"name":"mine","file":"../theirs/SKILL.md"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() => CallAsync(tools["skill_read"], mine, """{"name":"nobody"}"""));
    }

    [Fact]
    public async Task ScriptsAreSideEffectsAndNeverRunWithoutAnApproval()
    {
        var tools = new SkillFileTools(await StoreAsync()).CreateTools().ToDictionary(tool => tool.Name);
        var script = tools[SkillFileTools.RunScript];

        Assert.False(script.Descriptor.Risk.ReadOnly);
        Assert.True(script.Descriptor.Risk.Destructive);
        // No shell configured, and no approval on the context: refused either way, before anything runs.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CallAsync(script, new ToolCallContext("s", "c1"), """{"name":"theirs","script":"scripts/run.sh"}"""));
        Assert.Equal("'it'\\''s'", SkillFileTools.Quote("it's"));
    }
}
