using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
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
        // C-033: a trusted server's descriptions are asked about too; asking them goes to the decider and does not taint.
        Assert.Single(harness.Decisions("injection/mcp:notes"));
    }

    /// <summary>C-033: records what the turn withheld.</summary>
    private sealed class Review : IToolDescriptionReview
    {
        public List<ToolWithholding> Requests { get; } = [];

        public void Withhold(ToolWithholding request) => Requests.Add(request);
    }

    private static TestTool Weather(string name, int length = 0, bool outside = false)
    {
        var description = "Report the weather for a city.";
        if (length > description.Length)
            description += new string('x', length - description.Length);
        return ServerTool(outside, name, description);
    }

    // Only the server's tools are shortlisted, so the shortlist and the chunks are the test's.
    private static TurnOptions McpOnly => new() { ToolFilter = descriptor => descriptor.Origin is not null };

    private static TurnRunner Runner(TurnHarness harness, Review review)
    {
        var services = harness.Services();
        return new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = services.Approvals,
            Options = services.Options,
            DescriptionReview = review,
        });
    }

    private static double[] Pick(DecisionQuestion question, string name) =>
        [.. question.Options.Select(option => option.Name == name ? 0.9 : 0.1 / (question.Options.Length - 1))];

    [Fact]
    public async Task A_confident_injection_verdict_withholds_exactly_its_chunks_tools_from_the_generator_and_the_pick()
    {
        // 500-character descriptions: two lines fit a 1,200-character chunk, so the third tool is alone in chunk #2.
        var kev = new ScriptedDecider((key, question) => key switch
        {
            "injection/mcp:notes#2" => [0.9, 0.1],
            "injection/mcp:notes" => [0.1, 0.9],
            "tool" => Pick(question, "weather_c"),
            _ => null,
        });
        var generator = new FakeGenerator(Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, calibrated: kev, options: McpOnly, tools: tools =>
        {
            tools.Add(Weather("weather_a", 500));
            tools.Add(Weather("weather_b", 500));
            tools.Add(Weather("weather_c", 500));
        });
        var review = new Review();

        var result = await harness.RunAsync(Runner(harness, review), "weather report");

        var first = Assert.Single(harness.Decisions("injection/mcp:notes")).Question!.Instructions.GetString()!;
        Assert.Contains("- weather_a: ", first, StringComparison.Ordinal);
        Assert.Contains("- weather_b: ", first, StringComparison.Ordinal);
        Assert.DoesNotContain("weather_c", first, StringComparison.Ordinal);
        var second = Assert.Single(harness.Decisions("injection/mcp:notes#2"));
        Assert.Contains("- weather_c: ", second.Question!.Instructions.GetString()!, StringComparison.Ordinal);
        // The pick of the withheld tool falls back to the shortlist without it; no generator call of the turn offers it.
        Assert.Equal(["weather_a", "weather_b"], Assert.Single(generator.Calls).ToolNames.Order(StringComparer.Ordinal));
        var request = Assert.Single(review.Requests);
        Assert.Equal(("mcp:notes", "weather_c", second.DecisionId), (request.Origin, Assert.Single(request.Tools), request.DecisionId));
        // Trusted descriptions, and the withheld one was never shown to the generator.
        Assert.False(result.Tainted);
    }

    [Theory]
    [InlineData(0.3, true)]
    [InlineData(0.9, false)]
    public async Task An_answer_below_the_floor_or_uncalibrated_withholds_nothing(double yes, bool calibrated)
    {
        var kev = new ScriptedDecider((key, _) => key.StartsWith("injection/", StringComparison.Ordinal) ? [yes, 1 - yes] : null)
        {
            Calibrated = calibrated,
        };
        var generator = new FakeGenerator(Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, calibrated: kev, options: McpOnly, tools: tools => tools.Add(Weather("weather_a")));
        var review = new Review();

        await harness.RunAsync(Runner(harness, review), "weather report");

        Assert.Single(harness.Decisions("injection/mcp:notes"));
        Assert.Contains("weather_a", Assert.Single(generator.Calls).ToolNames);
        Assert.Empty(review.Requests);
    }

    [Fact]
    public async Task Chunks_are_whole_lines_asked_at_most_two_per_batch_and_the_rest_in_later_turns()
    {
        // Five 700-character lines: one per chunk. A 1,500-character line is cut and still alone in its chunk.
        var generator = new FakeGenerator(Reply.Say("One."), Reply.Say("Two."), Reply.Say("Three."), Reply.Say("Four."));
        using var harness = new TurnHarness(generator, options: McpOnly, tools: tools =>
        {
            tools.Add(Weather("weather_a", 1_500, outside: true));
            foreach (var name in (string[])["weather_b", "weather_c", "weather_d", "weather_e"])
                tools.Add(Weather(name, 700));
        });
        var runner = harness.Runner();
        IReadOnlyList<string> Asked() => [.. harness.Decisions().Select(decision => decision.QuestionId).Where(id => id.StartsWith("injection/", StringComparison.Ordinal))];

        var first = await harness.RunAsync(runner, "weather report");
        Assert.Equal(["injection/mcp:notes", "injection/mcp:notes#2"], Asked());
        // weather_a is an outside description: its chunk taints; the trusted chunks do not.
        Assert.True(first.Tainted);
        var cut = harness.Decisions("injection/mcp:notes")[0].Question!.Instructions.GetString()!;
        Assert.Contains("- weather_a: ", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("weather_b", cut, StringComparison.Ordinal);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.True(cut[(cut.IndexOf("- weather_a", StringComparison.Ordinal))..].Length <= 1_200);

        await harness.RunAsync(runner, "weather report");
        Assert.Equal(["injection/mcp:notes", "injection/mcp:notes#2", "injection/mcp:notes#3", "injection/mcp:notes#4"], Asked());
        await harness.RunAsync(runner, "weather report");
        Assert.Equal(5, Asked().Count);
        Assert.Equal("injection/mcp:notes#5", Asked()[^1]);
        await harness.RunAsync(runner, "weather report");
        Assert.Equal(5, Asked().Count);
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
