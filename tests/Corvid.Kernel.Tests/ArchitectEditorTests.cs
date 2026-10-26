using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>
/// A7, architect/editor turns (plan §6): a coding turn that escalates plans on the escalated model and edits on the session's
/// own; one re-plan for a stuck editor; the egress gate applies to the planner; a turn that never escalates is unchanged.
/// </summary>
public sealed class ArchitectEditorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Plan = "1. In notes/todo.txt replace 'milk' with 'oat milk'.";

    private static readonly TurnOptions Architect = new() { ArchitectEditor = true };

    // A tool that changes files, so the turn is a coding one; "note" is one of the harness's keywords, so it is shortlisted.
    private static TestTool EditFile() => TestTool.Create("edit_note_file", "Edit a note file in the project: replace text in it.",
        new ToolRisk(SideEffects.Filesystem, Network: false, Destructive: false),
        """{"type":"object","properties":{"path":{"type":"string"},"old":{"type":"string"},"new":{"type":"string"}},"required":["path","old","new"]}""",
        (_, _) => ValueTask.FromResult<object?>("edited"),
        new ToolParameter("path", null, true, false), new ToolParameter("old", null, true, false), new ToolParameter("new", null, true, false));

    private const string EditArguments = """{"path":"notes/todo.txt","old":"milk","new":"oat milk"}""";

    // Kev says the request is hard (difficulty acts: escalate); every other question falls below its floor.
    private static ScriptedDecider Hard(Func<string, double[]?>? more = null) => new((key, _) =>
        key == "difficulty" ? [0.01, 0.01, 0.03, 0.15, 0.80] : more?.Invoke(key));

    private static TurnHarness Harness(FakeGenerator small, FakeGenerator large, IDecider? decider, TurnOptions? options = null,
        int generatorContext = 8_192) =>
        new(small, escalation: large, calibrated: decider, options: options ?? Architect, prompter: FakePrompter.Always(true),
            tools: tools => tools.Add(EditFile()), generatorContext: generatorContext);

    [Fact]
    public async Task A_hard_coding_turn_plans_on_the_escalated_model_and_edits_on_the_sessions_own()
    {
        var small = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited the todo note.")) { Name = "small" };
        var large = new FakeGenerator(Reply.Say(Plan)) { Name = "large" };
        using var harness = Harness(small, large, Hard());

        var result = await harness.RunAsync("edit my note file: oat milk instead of milk");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Equal("Edited the todo note.", result.Text);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(new TurnPlan("gen-large", "gen-small", "difficulty", 1), plan);
        Assert.Equal(["gen-large: difficulty"], result.Escalations);

        // The planner was asked once, with no tools, for a plan, and its request is marked so the host does not stream it.
        var planned = Assert.Single(large.Calls);
        Assert.Empty(planned.ToolNames);
        Assert.Contains("You are the architect", planned.LastText, StringComparison.Ordinal);
        Assert.True((bool)planned.Options!.AdditionalProperties![ModelRequestKeys.Unstreamed]!);

        // The editor made the edit with the plan in its history and a note saying whose plan it is.
        var edit = small.Calls[0];
        Assert.Contains("edit_note_file", edit.ToolNames);
        Assert.Contains(Plan, edit.AllText, StringComparison.Ordinal);
        Assert.Contains("written by gen-large", edit.AllText, StringComparison.Ordinal);
        Assert.Contains("not the user's words", edit.AllText, StringComparison.Ordinal);
        Assert.False(edit.Options!.AdditionalProperties?.ContainsKey(ModelRequestKeys.Unstreamed) ?? false);

        // The log: escalation, the plan with its planner, the hand-back, then the editor's call, named for the editor.
        var payloads = harness.Payloads();
        var logged = Assert.Single(payloads.OfType<AssistantMessage>(), message => message.Plan is not null);
        Assert.Equal(Plan, logged.Text);
        Assert.Equal(plan, logged.Plan);
        Assert.Equal(["escalation", "editor"], payloads.OfType<ModelSwitched>().Select(change => change.Reason));
        var order = payloads.Select((payload, index) => (payload, index)).ToList();
        int At<T>(Func<T, bool> match) where T : SessionEvent => order.First(item => item.payload is T typed && match(typed)).index;
        Assert.True(At<ModelSwitched>(change => change.Reason == "escalation") < At<AssistantMessage>(message => message.Plan is not null));
        Assert.True(At<AssistantMessage>(message => message.Plan is not null) < At<ModelSwitched>(change => change.Reason == "editor"));
        Assert.True(At<ModelSwitched>(change => change.Reason == "editor") < At<ToolCallIssued>(_ => true));
        Assert.Equal("gen-small", payloads.OfType<ToolCallIssued>().Single().Generator);
        // The answer is an ordinary answer, after the plan.
        Assert.Null(payloads.OfType<AssistantMessage>().Last().Plan);
        Assert.Same(harness.GeneratorBinding, harness.Slots.Current(ModelSlots.Generator));
    }

    [Fact]
    public async Task The_planned_turn_is_reported_to_the_listener_with_its_plan()
    {
        var listener = new PlanListener();
        var small = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Done.")) { Name = "small" };
        var large = new FakeGenerator(Reply.Say(Plan)) { Name = "large" };
        using var harness = new TurnHarness(small, escalation: large, calibrated: Hard(), options: Architect,
            prompter: FakePrompter.Always(true), tools: tools => tools.Add(EditFile())) { Listener = listener };

        await harness.RunAsync("edit my note file: oat milk instead of milk");

        var (session, plan, text) = Assert.Single(listener.Plans);
        Assert.Equal(TurnHarness.Session, session);
        Assert.Equal("gen-large", plan.Planner);
        Assert.Equal(Plan, text);
    }

    [Fact]
    public async Task A_stuck_editor_gets_one_replan_then_the_turn_goes_on_as_an_ordinary_stuck_escalation()
    {
        // The editor gets stuck after each round of edits. The first time it is re-planned; the second time the turn escalates
        // as any stuck turn does, and the escalated model finishes.
        var small = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Call("edit_note_file", EditArguments)) { Name = "small" };
        var large = new FakeGenerator(Reply.Say(Plan), Reply.Say("2. Also fix the second line."), Reply.Say("Finished on the large model."))
        {
            Name = "large",
        };
        using var harness = Harness(small, large, Hard(key => key == "stuck" ? [0.9, 0.1] : null));

        var result = await harness.RunAsync("edit my note file: oat milk instead of milk");

        Assert.Equal("Finished on the large model.", result.Text);
        Assert.Equal([new TurnPlan("gen-large", "gen-small", "difficulty", 1), new TurnPlan("gen-large", "gen-small", "stuck", 2)], result.Plans);
        Assert.Equal(["gen-large: difficulty", "gen-large: stuck", "gen-large: stuck"], result.Escalations);
        Assert.Contains("got stuck", large.Calls[1].LastText, StringComparison.Ordinal);
        Assert.Empty(large.Calls[1].ToolNames);
        // Both rounds of edits were the editor's; the re-plan's own call had no tools, the last call is the large model's answer.
        Assert.Equal(["gen-small", "gen-small"], harness.Payloads().OfType<ToolCallIssued>().Select(call => call.Generator));
        Assert.Equal(2, harness.Payloads().OfType<AssistantMessage>().Count(message => message.Plan is not null));
        Assert.Equal(["escalation", "editor", "escalation", "editor", "escalation", "de_escalation"],
            harness.Payloads().OfType<ModelSwitched>().Select(change => change.Reason));
    }

    [Fact]
    public async Task An_editor_whose_context_does_not_fit_after_the_plan_stays_on_the_escalated_model_without_planning_again()
    {
        // The small model's window cannot hold the history: the context trigger escalates and plans; the editor still does
        // not fit, so the next context escalation keeps the turn on the large model, which makes the edit. No loop.
        var small = new FakeGenerator() { Name = "small" };
        var large = new FakeGenerator(Reply.Say(Plan), Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited on the large model."))
        {
            Name = "large",
        };
        using var harness = Harness(small, large, decider: null, generatorContext: 3_000);
        var filler = string.Concat(Enumerable.Repeat("a long earlier answer about the shopping list, ", 200));
        harness.Seed(new UserMessage("tell me about the list"), new AssistantMessage(filler));

        var result = await harness.RunAsync("edit my note file: oat milk instead of milk");

        Assert.Equal("Edited on the large model.", result.Text);
        Assert.Equal([new TurnPlan("gen-large", "gen-small", "context", 1)], result.Plans);
        Assert.Equal(["gen-large: context", "gen-large: context"], result.Escalations);
        Assert.Equal("gen-large", harness.Payloads().OfType<ToolCallIssued>().Single().Generator);
    }

    [Fact]
    public async Task A_turn_that_never_escalates_is_the_same_with_the_split_on_or_off()
    {
        async Task<(IReadOnlyList<GeneratorCall> Calls, IReadOnlyList<string> Events, TurnResult Result, int Large)> RunAsync(TurnOptions options)
        {
            var small = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited.")) { Name = "small" };
            var large = new FakeGenerator(Reply.Say(Plan)) { Name = "large" };
            using var harness = Harness(small, large, decider: null, options);
            var result = await harness.RunAsync("edit my note file: oat milk instead of milk");
            return (small.Calls, [.. harness.Payloads().Select(payload => payload.GetType().Name)], result, large.Calls.Count);
        }

        var on = await RunAsync(Architect);
        var off = await RunAsync(TurnOptions.Default);

        Assert.Equal(0, on.Large);
        Assert.Empty(on.Result.Plans);
        Assert.Empty(on.Result.Escalations);
        Assert.Equal(off.Events, on.Events);
        Assert.Equal(off.Result.Text, on.Result.Text);
        Assert.Equal(off.Calls.Select(call => call.AllText), on.Calls.Select(call => call.AllText));
        Assert.Equal(off.Calls.Select(call => call.Messages[0].Text), on.Calls.Select(call => call.Messages[0].Text));
    }

    [Fact]
    public async Task With_the_split_off_an_escalated_turn_runs_on_the_escalated_model_as_before()
    {
        var small = new FakeGenerator() { Name = "small" };
        var large = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited on the large model.")) { Name = "large" };
        using var harness = Harness(small, large, Hard(), TurnOptions.Default);

        var result = await harness.RunAsync("edit my note file: oat milk instead of milk");

        Assert.Empty(result.Plans);
        Assert.Equal("Edited on the large model.", result.Text);
        Assert.Empty(small.Calls);
        Assert.DoesNotContain(harness.Payloads().OfType<AssistantMessage>(), message => message.Plan is not null);
    }

    [Fact]
    public async Task A_turn_with_no_tool_that_changes_files_is_not_split()
    {
        var small = new FakeGenerator() { Name = "small" };
        var large = new FakeGenerator(Reply.Say("Here is the hard answer.")) { Name = "large" };
        using var harness = new TurnHarness(small, escalation: large, calibrated: Hard(), options: Architect);

        var result = await harness.RunAsync("list my notes");

        Assert.Empty(result.Plans);
        Assert.Equal("Here is the hard answer.", result.Text);
    }

    [Fact]
    public async Task Architect_asked_for_the_next_turn_plans_without_a_trigger_and_only_once()
    {
        var small = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited."), Reply.Say("Nothing to plan.")) { Name = "small" };
        var large = new FakeGenerator(Reply.Say(Plan)) { Name = "large" };
        // The split is off in the options and nothing escalates: only the person's request plans.
        using var harness = Harness(small, large, decider: null, TurnOptions.Default);
        var runner = harness.Runner();
        runner.Architect = ArchitectRequest.NextTurn;

        var first = await harness.RunAsync(runner, "edit my note file: oat milk instead of milk");
        var second = await harness.RunAsync(runner, "and now?");

        Assert.Equal([new TurnPlan("gen-large", "gen-small", TurnRunner.ArchitectTrigger, 1)], first.Plans);
        Assert.Equal(["gen-large: architect"], first.Escalations);
        Assert.Equal(ArchitectRequest.None, runner.Architect);
        Assert.Empty(second.Plans);
        Assert.Single(large.Calls);
    }

    [Fact]
    public async Task Architect_on_for_the_session_plans_every_turn_until_it_is_turned_off()
    {
        var small = new FakeGenerator(Reply.Say("One."), Reply.Say("Two."), Reply.Say("Three.")) { Name = "small" };
        var large = new FakeGenerator(Reply.Say("Plan one."), Reply.Say("Plan two.")) { Name = "large" };
        using var harness = Harness(small, large, decider: null, new TurnOptions { ToolFilter = _ => false });
        var runner = harness.Runner();
        runner.Architect = ArchitectRequest.Session;

        var first = await harness.RunAsync(runner, "edit my note file");
        var second = await harness.RunAsync(runner, "edit my note file again");
        runner.Architect = ArchitectRequest.None;
        var third = await harness.RunAsync(runner, "edit my note file once more");

        Assert.Single(first.Plans);
        Assert.Single(second.Plans);
        Assert.Empty(third.Plans);
        Assert.Equal(ArchitectRequest.None, runner.Architect);
    }

    [Fact]
    public async Task The_editor_model_from_the_options_applies_the_plan_when_the_slot_binds_it()
    {
        var small = new FakeGenerator() { Name = "small" };
        var large = new FakeGenerator(Reply.Say(Plan)) { Name = "large" };
        var editor = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited by the editor.")) { Name = "editor" };
        using var harness = Harness(small, large, Hard(), Architect with { EditorModel = "gen-editor" });
        var editorBinding = new ModelBinding(TurnHarness.Profile("gen-editor", ToolCallSupport.Native), editor);
        harness.Registry.Bind(ModelSlots.Generator, harness.GeneratorBinding, [harness.EscalationBinding!], [editorBinding]);

        var result = await harness.RunAsync("edit my note file: oat milk instead of milk");

        Assert.Equal("Edited by the editor.", result.Text);
        Assert.Equal(new TurnPlan("gen-large", "gen-editor", "difficulty", 1), Assert.Single(result.Plans));
        Assert.Empty(small.Calls);
        Assert.Equal("gen-editor", harness.Payloads().OfType<ToolCallIssued>().Single().Generator);
        // The turn's end returns the slot to the session's own model.
        Assert.Same(harness.GeneratorBinding, harness.Slots.Current(ModelSlots.Generator));
    }

    [Fact]
    public async Task The_egress_gate_refuses_a_tainted_planner_and_the_turn_edits_locally_without_a_plan()
    {
        // The same turn, from the user and from outside: a cloud planner whose policy sends PII as is but refuses it in a
        // tainted session. Only the tainted turn is refused, before anything reaches the planner.
        var trusted = await GatedTurnAsync(EnvelopeOrigin.User);
        Assert.Single(trusted.Large.Calls);
        Assert.Single(trusted.Result.Plans);

        var tainted = await GatedTurnAsync(EnvelopeOrigin.External);
        Assert.Empty(tainted.Large.Calls);
        Assert.Empty(tainted.Result.Plans);
        Assert.Equal(["gen-cloud: difficulty", "gen-small: privacy"], tainted.Result.Escalations);
        Assert.Equal("Edited locally.", tainted.Result.Text);
        Assert.True(tainted.Result.Tainted);
        // A7 e2e: the hand-back is a privacy retreat, not "editor": the app read "back to local: to apply the plan" with no plan.
        Assert.Equal(["escalation", "privacy"], tainted.Events.OfType<ModelSwitched>().Select(change => change.Reason));
        Assert.Equal("gen-small", tainted.Events.OfType<ToolCallIssued>().Single().Generator);
        Assert.DoesNotContain(tainted.Events.OfType<AssistantMessage>(), message => message.Plan is not null);
    }

    private static async Task<(FakeGenerator Large, TurnResult Result, IReadOnlyList<SessionEvent> Events)> GatedTurnAsync(EnvelopeOrigin origin)
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-architect-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SqliteEventLog(Path.Combine(root, "events.db"));
            var small = new FakeGenerator(Reply.Call("edit_note_file", EditArguments), Reply.Say("Edited locally.")) { Name = "small" };
            var large = new FakeGenerator(Reply.Say(Plan)) { Name = "large" };
            var person = new FakeExtractor(0.95f, ("person", "Alice Smith"));
            var cloud = new ModelProfile("gen-cloud", 200_000, 100_000, ToolCallSupport.Native, StructuredOutputSupport.None,
                PromptCachingStyle.None, ModelCost.Free, PrivacyClass.Cloud, "remote");
            var gate = new PrivacyEgress(person, new PrivacyEgressOptions
            {
                PolicyFor = _ => EgressPolicy.Default with { Action = EgressAction.Send, Tainted = EgressAction.Refuse },
            });
            var embedder = new KeywordEmbedder(TurnHarness.Keywords);
            var registry = new ModelSlotRegistry()
                .Bind(ModelSlots.Generator, new ModelBinding(TurnHarness.Profile("gen-small", ToolCallSupport.TemplateRendered), small),
                    [new ModelBinding(cloud, new PrivacyEgressChatClient(large, cloud, gate))])
                .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
            // The switch-time hook allows the move, so the request gate is what refuses.
            var slots = new SessionModelSlots(registry, log, TurnHarness.Session, new ModelSwitchOptions { Privacy = PrivacyGates.AllowAll });
            var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, small), log, Hard());
            var tools = new ToolRegistry();
            tools.Add(EditFile());
            var services = new TurnServices
            {
                Log = log,
                Slots = slots,
                Fabric = fabric,
                Tools = tools,
                Approvals = new Approvals.ApprovalPipeline(Approvals.ApprovalEnvironment.Create(TurnHarness.Workspace, "/home/user"), fabric, log,
                    FakePrompter.Always(true)),
                Options = Architect,
            };
            var result = await new TurnRunner(TurnHarness.Session, services).RunAsync(
                new Envelope(TurnHarness.Session, "cli", "edit my note file for Alice Smith: oat milk instead of milk", origin), Ct);
            return (large, result, [.. log.Read(TurnHarness.Session, limit: 4096).Select(item => item.Payload)]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_plan_is_an_assistant_message_with_its_plan_and_an_answer_keeps_its_persisted_form()
    {
        const string answer = """{"type":"assistant_message","text":"Done."}""";
        var older = Assert.IsType<AssistantMessage>(EventJson.Deserialize(EventJson.Version, answer));
        Assert.Null(older.Plan);
        Assert.Equal(answer, EventJson.Serialize(older));

        var json = EventJson.Serialize(new AssistantMessage("1. Edit a.txt.", Plan: new TurnPlan("gen-large", "gen-small", "difficulty")));
        Assert.Equal("""{"type":"assistant_message","text":"1. Edit a.txt.","plan":{"planner":"gen-large","editor":"gen-small","trigger":"difficulty","attempt":1}}""",
            json);
        Assert.Equal(new TurnPlan("gen-large", "gen-small", "difficulty"), Assert.IsType<AssistantMessage>(EventJson.Deserialize(EventJson.Version, json)).Plan);
    }

    private sealed class PlanListener : ITurnListener
    {
        public List<(string Session, TurnPlan Plan, string Text)> Plans { get; } = [];

        public void Planned(string sessionId, TurnPlan plan, string text) => Plans.Add((sessionId, plan, text));
    }
}
