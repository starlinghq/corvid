using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Kernel.Turns;
using Corvid.Models;

namespace Corvid.Kernel.Tests;

/// <summary>A5: the decider per session, and a turn that may not ask the person anything.</summary>
public sealed class SessionDeciderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModelBinding Decider(IDecider decider) =>
        new(new ModelProfile(decider.Name + "@cpu", 4_096, 4_096, ToolCallSupport.None, StructuredOutputSupport.None, PromptCachingStyle.None,
            ModelCost.Free, PrivacyClass.Local, "cpu"), decider);

    private static TurnServices Services(TurnHarness harness, SessionModelSlots slots, TurnOptions? options = null) => new()
    {
        Log = harness.Log,
        Slots = slots,
        Fabric = harness.Fabric,
        Deciders = new SessionDeciders(harness.Fabric, harness.Registry),
        Tools = harness.Tools,
        Approvals = harness.Approvals,
        Options = options ?? harness.Options,
    };

    private static Task<TurnResult> RunAsync(TurnServices services, string text) =>
        new TurnRunner(TurnHarness.Session, services).RunAsync(new Envelope(TurnHarness.Session, "cli", text), Ct);

    [Fact]
    public async Task A_session_without_a_decider_of_its_own_runs_on_the_shared_fabric()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("hi")));
        var deciders = new SessionDeciders(harness.Fabric, harness.Registry);

        Assert.Same(harness.Fabric, deciders.For(harness.Slots));
        await RunAsync(Services(harness, harness.Slots), "hello");

        Assert.All(harness.Decisions(), decision => Assert.StartsWith("degraded", decision.Decider, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_mix_decider_answers_the_sessions_checkpoints_and_its_decisions_are_still_logged()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("hi")));
        var own = new ScriptedDecider((_, question) => [.. question.Options.Select(_ => 1.0 / question.Options.Length)]);
        var slots = new SessionModelSlots(harness.Registry, harness.Log, TurnHarness.Session,
            slots: new Dictionary<string, SlotConfiguration> { [ModelSlots.Decider] = new(ModelSlots.Decider, Decider(own), [], []) });

        await RunAsync(Services(harness, slots), "hello");

        var ingress = harness.Decisions("intent");
        Assert.Equal("kev-test", Assert.Single(ingress).Decider);
        Assert.Null(harness.Fabric.CalibratedDecider);
    }

    [Fact]
    public async Task Switching_the_decider_for_the_session_takes_effect_at_the_next_checkpoint()
    {
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("one"), Reply.Say("two")));
        var services = Services(harness, harness.Slots);
        await RunAsync(services, "first");

        var switched = await harness.Slots.SetSessionDefaultAsync(ModelSlots.Decider,
            Decider(new ScriptedDecider((_, question) => [.. question.Options.Select(_ => 1.0 / question.Options.Length)])), cancellationToken: Ct);
        await RunAsync(services, "second");

        Assert.Equal(SwitchStatus.Switched, switched.Status);
        Assert.Equal(["degraded", "kev-test"], harness.Decisions("intent").Select(decision => decision.Decider!.Split(' ')[0]));
        Assert.Equal("session", harness.Payloads().OfType<ModelSwitched>().Single().Reason);
    }

    [Fact]
    public async Task The_sessions_decider_answers_its_approvals_risk_question()
    {
        var generator = new FakeGenerator(Reply.Call("post_webhook", """{"url":"https://hooks.example/a","body":"hi"}"""), Reply.Say("Posted."));
        var webhook = TestTool.PostWebhook();
        var prompter = FakePrompter.Always(approve: false);
        using var harness = new TurnHarness(generator, prompter: prompter, options: new TurnOptions { ApprovalMode = ApprovalMode.Permissive },
            tools: tools => tools.Add(webhook));
        var slots = new SessionModelSlots(harness.Registry, harness.Log, TurnHarness.Session,
            slots: new Dictionary<string, SlotConfiguration> { [ModelSlots.Decider] = new(ModelSlots.Decider, Decider(new RiskOnlyDecider(0)), [], []) });

        await RunAsync(Services(harness, slots), "post hi to the webhook");

        // Permissive mode with a calibrated level-0 risk answer runs unasked: only the session's decider gives one.
        Assert.Empty(prompter.Prompts);
        Assert.Equal(1, webhook.Invocations);
        Assert.Equal("kev-test", Assert.Single(harness.Decisions("risk")).Decider);
    }

    [Fact]
    public async Task A_turn_that_may_not_ask_is_never_diverted_into_a_question()
    {
        var generator = new FakeGenerator(Reply.Say("Moved the reminder to Friday."));
        using var harness = new TurnHarness(generator, calibrated: new ScriptedDecider((key, question) => key switch
        {
            "clarify" => [0.9, 0.1],
            "intent" => [.. question.Options.Select(option => option.Name == "ask_clarifying_question" ? 0.97 : 0.01)],
            _ => null,
        }), options: new TurnOptions { AsksQuestions = false });

        var result = await harness.RunAsync("move it");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Empty(harness.Decisions("clarify"));
        Assert.Empty(harness.Payloads().OfType<ClarificationAsked>());
    }

    [Fact]
    public async Task A_turn_that_may_not_ask_is_not_offered_ask_user()
    {
        var generator = new FakeGenerator(Reply.Say("Done."));
        using var harness = new TurnHarness(generator, calibrated: new ScriptedDecider((_, _) => null),
            options: new TurnOptions { AsksQuestions = false }, tools: tools => tools.Add(new AskUserTool()));

        await harness.RunAsync("tag the release");

        Assert.NotEmpty(generator.Calls[0].ToolNames);
        Assert.DoesNotContain(AskUserTool.ToolName, generator.Calls[0].ToolNames);
    }
}
