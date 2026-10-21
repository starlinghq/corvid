using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;
using Corvid.Models.Privacy;
using Corvid.Tools;

namespace Corvid.Kernel.Tests;

/// <summary>C-022 (§10.3): tool and skill descriptions from outside taint the turn that shows them to the generator.</summary>
public sealed class DescriptionTaintTests
{
    private static TestTool ServerTool(bool outside, string name = "notes_list", string description = "List the notes kept on the server.") =>
        new(new ToolDescriptor(name, description, """{"type":"object","properties":{}}""",
            new ToolRisk(SideEffects.Remote, Network: false, Destructive: true, OutsideDescription: outside), [])
        { Origin = "mcp:notes" }, (_, _) => ValueTask.FromResult<object?>("[]"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tool_description_from_outside_taints_the_turn_that_offers_it_without_a_call(bool outside)
    {
        var generator = new FakeGenerator(Reply.Say("You have two notes."));
        using var harness = new TurnHarness(generator, tools: tools => tools.Add(ServerTool(outside)));

        var result = await harness.RunAsync("list my notes");

        var call = Assert.Single(generator.Calls);
        Assert.Contains("notes_list", call.ToolNames);
        Assert.Equal(outside, result.Tainted);
        // The very request that carries the description says so, for the egress gate and Vault.
        Assert.Equal(outside, (bool)call.Options!.AdditionalProperties![ModelRequestKeys.Tainted]!);
        Assert.Equal(outside ? 1 : 0, harness.Decisions("injection/mcp:notes").Count);
    }

    [Fact]
    public async Task Injection_is_asked_about_a_servers_descriptions_once_per_session_in_the_ingress_batch()
    {
        var generator = new FakeGenerator(Reply.Say("Two notes."), Reply.Say("Still two."));
        using var harness = new TurnHarness(generator, tools: tools => tools.Add(ServerTool(outside: true)));
        var runner = harness.Runner();

        await harness.RunAsync(runner, "list my notes");
        var second = await harness.RunAsync(runner, "list my notes again");

        var decision = Assert.Single(harness.Decisions("injection/mcp:notes"));
        Assert.Contains("notes_list: List the notes kept on the server.", decision.Question!.Instructions.GetString(), StringComparison.Ordinal);
        // Asked in the same batch as the first turn's other ingress questions, not in a round of its own: its decision is
        // in the same run of decisions as that turn's intent.
        var payloads = harness.Payloads();
        var intent = payloads.Select((payload, index) => (payload, index)).First(item => item.payload is Decision { QuestionId: "intent" }).index;
        var injection = payloads.Select((payload, index) => (payload, index)).Single(item => item.payload is Decision { QuestionId: "injection/mcp:notes" }).index;
        Assert.All(payloads.Skip(Math.Min(intent, injection)).Take(Math.Abs(injection - intent) + 1), payload => Assert.IsType<Decision>(payload));
        // The second turn is tainted by showing the description again, not by a second question.
        Assert.True(second.Tainted);
    }

    private sealed class Skills(params SkillOption[] options) : ISkillSource
    {
        public IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools) => options;

        public SkillBody? Load(string name) => null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_L0_index_that_lists_a_hub_skill_taints_the_turn(bool trusted)
    {
        var generator = new FakeGenerator(Reply.Say("Here is the weather."));
        using var harness = new TurnHarness(generator);
        var services = harness.Services();
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = services.Approvals,
            Options = services.Options,
            Skills = new Skills(new SkillOption("weather-report", "Write a weather report for a city.", [], trusted)),
        });

        var result = await harness.RunAsync(runner, "write the weather report");

        Assert.Contains("weather-report", Assert.Single(generator.Calls).AllText, StringComparison.Ordinal);
        Assert.Equal(!trusted, result.Tainted);
    }
}
