using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Turns;
using Corvid.Models.Privacy;
using Corvid.Tools;

namespace Corvid.Kernel.Tests;

/// <summary>C-022 (§10.3): tool and skill descriptions from outside taint the turn that shows them to the generator.</summary>
public sealed class DescriptionTaintTests
{
    private static TestTool ServerTool(bool outside, string name = "notes_list", string description = "List the notes kept on the server.",
        string origin = "mcp:notes") =>
        new(new ToolDescriptor(name, description, """{"type":"object","properties":{}}""",
            new ToolRisk(SideEffects.Remote, Network: false, Destructive: true, OutsideDescription: outside), [])
        { Origin = origin }, (_, _) => ValueTask.FromResult<object?>("[]"));

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
        // C-037: only an outside description is asked about; a trusted server's gets C-022's pattern scan and pins at listing.
        Assert.Equal(outside ? 1 : 0, harness.Decisions("injection/mcp:notes").Count);
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

    private static double[] Pick(DecisionQuestion question, string name) =>
        [.. question.Options.Select(option => option.Name == name ? 0.9 : 0.1 / (question.Options.Length - 1))];

    private static IReadOnlyList<string> DescriptionKeys(TurnHarness harness) =>
        [.. harness.Decisions().Select(decision => decision.QuestionId).Where(id => id.StartsWith("injection/", StringComparison.Ordinal))];

    [Fact]
    public async Task A_confident_injection_verdict_on_an_outside_description_taints_and_withholds_nothing()
    {
        // C-037: the generic question answers yes on benign descriptions (spike S2), so its verdict is logged, not acted on.
        var kev = new ScriptedDecider((key, question) => key switch
        {
            "injection/mcp:notes" => [0.9, 0.1],
            "tool" => Pick(question, "weather_a"),
            _ => null,
        });
        var tool = Weather("weather_a", outside: true);
        var generator = new FakeGenerator(Reply.Call("weather_a"), Reply.Say("Sunny."));
        var prompter = FakePrompter.Always(approve: true);
        using var harness = new TurnHarness(generator, calibrated: kev, prompter: prompter, options: McpOnly, tools: tools => tools.Add(tool));

        var result = await harness.RunAsync("weather report");

        var decision = Assert.Single(harness.Decisions("injection/mcp:notes"));
        Assert.Equal("mark_tainted", decision.Action);
        Assert.True(result.Tainted);
        // Still in the tool question's options, still offered to the generator, and still in the registry.
        Assert.Contains(harness.Decisions("tool")[0].Question!.Options, option => option.Name == "weather_a");
        Assert.Contains("weather_a", generator.Calls[0].ToolNames);
        Assert.True(harness.Tools.TryGet("weather_a", out _));
        // An untrusted server's tool is not read-only: its call is asked about (every call), then runs.
        Assert.Single(prompter.Prompts);
        Assert.Contains(harness.Payloads(), payload => payload is ApprovalRequested);
        Assert.Equal(1, tool.Invocations);
    }

    [Fact]
    public async Task A_trusted_servers_descriptions_are_not_asked_about_and_do_not_taint()
    {
        var kev = new ScriptedDecider((key, _) => key.StartsWith("injection/", StringComparison.Ordinal) ? [0.9, 0.1] : null);
        var generator = new FakeGenerator(Reply.Say("Done."));
        using var harness = new TurnHarness(generator, calibrated: kev, options: McpOnly, tools: tools =>
        {
            tools.Add(ServerTool(outside: false, "alpha_a", "Read alpha.", "mcp:alpha"));
            tools.Add(Weather("weather_a"));
        });

        var result = await harness.RunAsync("read everything");

        Assert.Empty(DescriptionKeys(harness));
        Assert.False(result.Tainted);
        Assert.Equal(["alpha_a", "weather_a"], Assert.Single(generator.Calls).ToolNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Outside_chunks_are_whole_lines_asked_beyond_any_cap_and_once_per_session()
    {
        // Five 700-character lines: one per chunk. A 1,500-character line is cut and still alone in its chunk. Three more
        // origins with an outside description, and one trusted origin that is never asked.
        var generator = new FakeGenerator(Reply.Say("One."), Reply.Say("Two."));
        using var harness = new TurnHarness(generator, options: McpOnly, tools: tools =>
        {
            tools.Add(Weather("weather_a", 1_500, outside: true));
            foreach (var name in (string[])["weather_b", "weather_c", "weather_d", "weather_e"])
                tools.Add(Weather(name, 700, outside: true));
            foreach (var origin in (string[])["a", "b", "c"])
                tools.Add(ServerTool(outside: true, origin + "_read", "Read " + origin + ".", "mcp:" + origin));
            tools.Add(ServerTool(outside: false, "t_read", "Read t.", "mcp:t"));
        });
        var services = harness.Services();
        // A shortlist that holds every tool, so every chunk is in the turn.
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = services.Approvals,
            Options = services.Options,
            Shortlist = new ToolShortlist(new ShortlistOptions(Size: 16)),
        });

        var first = await harness.RunAsync(runner, "weather report");

        Assert.Equal(["injection/mcp:a", "injection/mcp:b", "injection/mcp:c", "injection/mcp:notes", "injection/mcp:notes#2",
                "injection/mcp:notes#3", "injection/mcp:notes#4", "injection/mcp:notes#5"],
            DescriptionKeys(harness).Order(StringComparer.Ordinal));
        Assert.True(first.Tainted);
        var cut = harness.Decisions("injection/mcp:notes")[0].Question!.Instructions.GetString()!;
        Assert.Contains("- weather_a: ", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("weather_b", cut, StringComparison.Ordinal);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.True(cut[(cut.IndexOf("- weather_a", StringComparison.Ordinal))..].Length <= 1_200);

        await harness.RunAsync(runner, "weather report");
        Assert.Equal(8, DescriptionKeys(harness).Count);
    }

    [Fact]
    public async Task A_tool_held_before_the_turn_can_never_be_picked_or_called_in_it()
    {
        // C-037: every withholding is decided by the tools' source before the turn reads the registry (BuildShortlistAsync
        // snapshots it once into the catalogue, and nothing after ingress removes from the catalogue or the shortlist). Here
        // the hold is McpClients' (Withhold or a pin mismatch): the tool leaves the registry before the turn. Kev is scripted
        // to pick it and the generator to call it anyway.
        var held = Weather("weather_b", outside: true);
        var kev = new ScriptedDecider((key, question) => key switch
        {
            "tool" => Pick(question, "weather_b"),
            _ => null,
        });
        var generator = new FakeGenerator(Reply.Call("weather_b"), Reply.Say("Sunny."));
        using var harness = new TurnHarness(generator, calibrated: kev, options: McpOnly, tools: tools =>
        {
            tools.Add(Weather("weather_a"));
            tools.Add(held);
        });
        harness.Tools.Remove("weather_b");

        await harness.RunAsync("weather report");

        Assert.DoesNotContain(Assert.Single(harness.Decisions("tool")).Question!.Options, option => option.Name == "weather_b");
        Assert.All(generator.Calls, call => Assert.DoesNotContain("weather_b", call.ToolNames));
        // The call is refused as naming no offered tool, and the held tool never ran.
        var issued = Assert.Single(harness.Payloads().OfType<ToolCallIssued>(), call => call.ToolName == "weather_b");
        Assert.Null(issued.ReadOnly);
        var refused = Assert.Single(harness.Payloads().OfType<ToolResult>(), result => result.CallId == issued.CallId);
        Assert.True(refused.IsError);
        Assert.Contains("is not one of the tools offered", refused.Result.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, held.Invocations);
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
