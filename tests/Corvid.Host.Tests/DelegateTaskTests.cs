using System.Collections.Concurrent;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>A5: the <c>delegate_task</c> tool (plan §14, §17 row 1.7) end to end through the turn loop and the host.</summary>
public sealed class DelegateTaskTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Approves every prompt and keeps it.</summary>
    private sealed class Recording(bool approve = true) : IApprovalPrompter
    {
        public ConcurrentQueue<ApprovalPrompt> Prompts { get; } = new();

        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return ValueTask.FromResult(approve ? ApprovalAnswer.Approve() : ApprovalAnswer.Deny());
        }
    }

    /// <summary>A network read: its result is outside content, so a child that calls it is tainted.</summary>
    private sealed class FetchTool : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() =>
        [
            new TestTool(new ToolDescriptor("fetch_page", "Fetch a web page.", """{"type":"object","properties":{}}""",
                new ToolRisk(SideEffects.None, Network: true, Destructive: false), []), _ => "<html>outside</html>"),
        ];
    }

    private static TestHost Host(FakeModels models, Dictionary<string, string?>? settings = null, bool fetch = false)
    {
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Corvid:Tools:Delegate"] = "true",
            ["Corvid:Approvals:Mode"] = "Manual",
        };
        foreach (var (key, value) in settings ?? [])
            all[key] = value;
        return TestHost.Create(models, all, services =>
        {
            if (fetch)
                services.AddSingleton<IToolProvider>(new FetchTool());
        });
    }

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static IReadOnlyList<SessionEvent> Events(CorvidRuntime runtime, string sessionId) =>
        [.. runtime.Log.Read(sessionId, limit: 4096, cancellationToken: Ct).Select(item => item.Payload)];

    private static JsonElement ResultOf(CorvidRuntime runtime, string sessionId) =>
        Events(runtime, sessionId).OfType<ToolResult>().Last().Result;

    [Fact]
    public async Task The_agent_delegates_a_goal_and_reads_the_childs_answer_as_the_tool_result()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count my notes", tools = new[] { "list_notes", "delegate_task", "add_note" } })),
            // The child: one call, then its answer.
            Reply.Call("list_notes"),
            Reply.Say("You have two notes."),
            Reply.Say("Your helper says you have two notes.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var prompts = new Recording();
        using var attached = runtime.AttachPrompter(parent.Id, prompts);

        var delivery = await parent.SendAsync("ask a helper to count my notes", Ct);

        Assert.Equal("Your helper says you have two notes.", delivery.Result!.Text);
        // The spawn asked first (Manual mode), with the call's arguments on the card.
        var prompt = Assert.Single(prompts.Prompts, item => item.SessionId == parent.Id);
        Assert.Equal(DelegateTaskTool.ToolName, Assert.IsType<ToolAction>(prompt.Action).Tool.Name);
        var spawned = Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>());
        // An explicit toolset: delegate_task is never a child's (depth 1). add_note was asked for and kept.
        Assert.Equal(["list_notes", "add_note"], spawned.Tools);
        Assert.Equal(ChildSessions.ByAgent, spawned.By);
        Assert.NotNull(spawned.CallId);
        var result = ResultOf(runtime, parent.Id);
        Assert.Equal(spawned.ChildId, result.GetProperty("child_id").GetString());
        Assert.Equal("answered", result.GetProperty("status").GetString());
        Assert.Equal("You have two notes.", result.GetProperty("answer").GetString());
        Assert.Equal(ChildStatuses.Answered, Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>()).Status);
        // The child's turn was offered its own toolset only, without delegate_task.
        Assert.DoesNotContain(models.Generator.OfferedTools[1], name => name == DelegateTaskTool.ToolName);
        // Finished, it is its parent's record: no surface opens it by id as a session of its own (with the full toolset).
        var reopened = Assert.Throws<SessionHeldException>(() => runtime.OpenSession(spawned.ChildId, "acp"));
        Assert.Contains("child session: read-only", reopened.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_spawn_starts_no_child()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count my notes" })), Reply.Say("ok")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording(approve: false));

        await parent.SendAsync("ask a helper", Ct);

        Assert.Empty(Events(runtime, parent.Id).OfType<ChildSpawned>());
        Assert.True(Events(runtime, parent.Id).OfType<ToolResult>().Single().IsError);
    }

    [Fact]
    public async Task Several_tasks_run_at_once()
    {
        // Each child's first generator call waits until both children have made theirs: run one after the other, they would not.
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { tasks = new[] { new { goal = "first part" }, new { goal = "second part" } } })),
            Reply.Say("part done"),
            Reply.Say("part done"),
            Reply.Say("Both parts are done."))
        {
            Hold = (step, token) =>
            {
                if (step is not (1 or 2))
                    return null;
                if (Interlocked.Increment(ref arrived) == 2)
                    both.TrySetResult();
                return both.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            },
        };
        await using var host = Host(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        var delivery = await parent.SendAsync("do both parts", Ct);

        Assert.Equal("Both parts are done.", delivery.Result!.Text);
        var children = ResultOf(runtime, parent.Id).GetProperty("children").EnumerateArray().ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, child => Assert.Equal("answered", child.GetProperty("status").GetString()));
        Assert.Equal(2, Events(runtime, parent.Id).OfType<ChildSpawned>().Count());
    }

    [Fact]
    public async Task An_answer_outside_the_output_schema_gets_one_repair_turn()
    {
        var schema = new { type = "object", properties = new { count = new { type = "integer" } }, required = new[] { "count" } };
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count", output_schema = schema })),
            Reply.Say("There are two."),
            messages =>
            {
                // The repair names what was wrong.
                Assert.Contains("does not meet the output schema", messages[^1].Text, StringComparison.Ordinal);
                return Reply.Text("""{"count": 2}""");
            },
            Reply.Say("Two.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        await parent.SendAsync("count", Ct);

        var result = ResultOf(runtime, parent.Id);
        Assert.Equal("answered", result.GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("output").GetProperty("count").GetInt32());
        var finished = Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>());
        Assert.True(finished.OutputValid);
    }

    [Fact]
    public async Task A_second_answer_outside_the_schema_fails()
    {
        var schema = new { type = "object", properties = new { count = new { type = "integer" } }, required = new[] { "count" } };
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count", output_schema = schema })),
            Reply.Say("There are two."),
            Reply.Say("Still two."),
            Reply.Say("It did not work.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        await parent.SendAsync("count", Ct);

        var result = ResultOf(runtime, parent.Id);
        Assert.Equal(ChildStatuses.InvalidOutput, result.GetProperty("status").GetString());
        Assert.Contains("output schema", result.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>()).OutputValid);
    }

    [Fact]
    public async Task A_childs_outside_content_taints_the_parent_through_the_result()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "read the page", tools = new[] { "fetch_page" } })),
            Reply.Call("fetch_page"),
            Reply.Say("The page says outside."),
            Reply.Say("Done.")));
        await using var host = Host(models, fetch: true);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());
        Assert.False(parent.Tainted);

        await parent.SendAsync("read it with a helper", Ct);

        Assert.True(Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>()).Tainted);
        Assert.True(parent.Tainted);
    }

    [Fact]
    public async Task Interrupting_the_parent_stops_its_children()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "work for a long time" })),
            Reply.Say("never seen"))
        {
            Hold = (step, token) =>
            {
                if (step != 1)
                    return null;
                childStarted.TrySetResult();
                // Held until cancelled: the parent's interrupt must reach the child's turn.
                return Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = Host(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        var delivery = await parent.PostAsync("start a long helper", EnvelopeOrigin.User, Ct);
        await childStarted.Task.WaitAsync(Ct);
        parent.Cancel();
        await delivery.WaitAsync(Ct);

        var finished = Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>());
        Assert.Equal(ChildStatuses.Cancelled, finished.Status);
        Assert.DoesNotContain(runtime.Children(parent.Id), child => child.Running);
    }

    [Fact]
    public async Task A_person_can_stop_one_child()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "work for a long time" })),
            Reply.Say("never seen"),
            Reply.Say("The helper was stopped."))
        {
            Hold = (step, token) =>
            {
                if (step != 1)
                    return null;
                childStarted.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = Host(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        var delivery = await parent.PostAsync("start a long helper", EnvelopeOrigin.User, Ct);
        await childStarted.Task.WaitAsync(Ct);
        var running = Assert.Single(runtime.Children(parent.Id));
        Assert.True(running.Running);
        Assert.True(runtime.StopChild(running.ChildId));
        var done = await delivery.WaitAsync(Ct);

        Assert.Equal("The helper was stopped.", done.Result!.Text);
        var result = ResultOf(runtime, parent.Id);
        Assert.Equal(ChildStatuses.Cancelled, result.GetProperty("status").GetString());
        Assert.Equal("stopped by the person", result.GetProperty("detail").GetString());
        Assert.False(runtime.StopChild(running.ChildId));
        Assert.Equal(ChildStatuses.Cancelled, Assert.Single(runtime.Children(parent.Id)).Status);
    }

    [Fact]
    public async Task The_stall_monitor_ends_a_child_that_makes_no_progress()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "loop", tools = new[] { "list_notes" }, mix = new { generator = new { model = "fake-gen", escalate = false } } })),
            Reply.Call("list_notes"),
            Reply.Say("I could not finish."),
            Reply.Say("The helper got stuck.")));
        await using var host = Host(models, new() { ["Corvid:Models:Slots:decider:Model"] = "fake-kev" });
        await using var runtime = await host.StartAsync();
        // The decider says the child is making no progress at its step checkpoint.
        models.Kev.Answer = (key, question) => key == "stuck" && question.Options.Any(option => option.Name == "yes")
            ? [.. question.Options.Select(option => option.Name == "yes" ? 1.0 : 0.0)]
            : [.. question.Options.Select(_ => 1.0 / question.Options.Length)];
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        await parent.SendAsync("loop with a helper", Ct);

        var finished = Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>());
        Assert.Equal(ChildStatuses.Stuck, finished.Status);
        // The child's decisions are logged in its own log, for calibration.
        Assert.Contains(Events(runtime, finished.ChildId).OfType<Decision>(), decision => decision.QuestionId == "stuck");
    }

    [Fact]
    public async Task A_guest_turn_is_never_offered_delegate_task_and_the_gateway_refuses_it()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello, guest.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        await (await session.PostAsGuestAsync("help me", new GuestScope(["list_notes", DelegateTaskTool.ToolName]), Ct)).WaitAsync(Ct);

        Assert.All(models.Generator.OfferedTools, offered => Assert.DoesNotContain(DelegateTaskTool.ToolName, offered));
        Assert.Contains("cannot be a guest tool", CorvidGateway.GuestToolShapeProblem(DelegateTaskTool.ToolName, new HashSet<string>()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_calling_turns_offer_is_the_ceiling_of_the_childs_toolset()
    {
        // A tool the parent session has but this turn was not offered (no ToolFilter here, so the offer is the catalogue:
        // ask_user is withheld in a non-interactive turn).
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "go", toolset = "read_only" })),
            Reply.Say("done"),
            Reply.Say("ok")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Recording());

        await parent.SendAsync("go", Ct);

        // read_only: the parent's tools that change nothing and read no network (add_note writes, so it is left out).
        var tools = Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>()).Tools;
        Assert.Contains("list_notes", tools);
        Assert.DoesNotContain("add_note", tools);
        var previews = await runtime.PreviewDelegationAsync(parent.Id, JsonSerializer.SerializeToElement(new { goal = "go", toolset = "nope" }), Ct);
        Assert.Contains("no toolset named", Assert.Single(previews).Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_preview_shows_the_clamped_plan_the_person_approves()
    {
        await using var host = Host(new FakeModels(), new() { ["Corvid:Agents:MaxSteps"] = "3" });
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();

        var previews = await runtime.PreviewDelegationAsync(parent.Id, JsonSerializer.SerializeToElement(new
        {
            tasks = new object[]
            {
                new { goal = "first", tools = new[] { "list_notes" }, max_steps = 50, mix = new { generator = "fake-gen-large" } },
                new { goal = "second", mix = new { generator = "no-such-model" } },
            },
        }), Ct);

        Assert.Equal(2, previews.Count);
        var plan = previews[0].Plan!;
        Assert.Equal(3, plan.Budget.MaxSteps);
        Assert.Equal(["list_notes"], plan.Tools);
        Assert.Equal("fake-gen-large", plan.Slots.Single(slot => slot.Slot == ModelSlots.Generator).Model);
        Assert.Contains("no model named", previews[1].Refusal, StringComparison.Ordinal);
        // Nothing was started or logged by a preview.
        Assert.Empty(Events(runtime, parent.Id).OfType<ChildSpawned>());
    }

    [Fact]
    public async Task The_preview_loads_no_model_the_mix_names()
    {
        // A5 fix round (D5): the card waits on the person, so it reads the catalogue and binds nothing; the approved call loads.
        var models = new FakeModels();
        models.Provider.Add("fake-gen-other", [ModelSlots.Generator], () => models.Larger);
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        Assert.DoesNotContain("fake-gen-other", models.Provider.Loaded);

        var previews = await runtime.PreviewDelegationAsync(parent.Id, JsonSerializer.SerializeToElement(new
        {
            goal = "go",
            mix = new { generator = "fake-gen-other" },
        }), Ct);

        Assert.Equal("fake-gen-other", Assert.Single(previews).Plan!.Slots.Single(slot => slot.Slot == ModelSlots.Generator).Model);
        Assert.DoesNotContain("fake-gen-other", models.Provider.Loaded);
        var plan = await runtime.PlanChildAsync(parent.Id, new ChildRequest("go") { Mix = ModelMix.Parse("""{"generator":"fake-gen-other"}""") }, Ct);
        Assert.Contains("fake-gen-other", models.Provider.Loaded);
        static IEnumerable<string> Shown(ChildPlan shown) =>
            shown.Slots.Select(slot => $"{slot.Slot}={slot.Model} {slot.Privacy} [{string.Join(",", slot.Ladder)}] {slot.Escalate}");
        Assert.Equal(Shown(previews[0].Plan!), Shown(plan));
    }

    private static ValueTask<Corvid.Host.Commands.CommandOutput> Command(CorvidRuntime runtime, CorvidSession session, string line)
    {
        var commands = Corvid.Host.Commands.CoreCommands.Register(new Corvid.Host.Commands.SlashCommandRegistry());
        return commands.ExecuteAsync(line, new Corvid.Host.Commands.SlashCommandContext(runtime, session, commands), Ct);
    }

    [Fact]
    public async Task Delegate_starts_a_child_with_the_persons_mix_and_agents_lists_shows_and_stops_it()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(Reply.Say("Found three."))
        {
            Hold = (step, token) =>
            {
                started.TrySetResult();
                return gate.Task.WaitAsync(token);
            },
        };
        var models = new FakeModels(generator);
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var ended = new TaskCompletionSource<ChildFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.ChildChanged += (id, update) =>
        {
            if (id == parent.Id && update is Corvid.Host.Sessions.ChildEnded end)
                ended.TrySetResult(end.Child);
        };

        var output = await Command(runtime, parent, "/delegate --model generator=fake-gen --tools list_notes --steps 3 look up the birthday of Ada");
        await started.Task.WaitAsync(Ct);
        var listed = await Command(runtime, parent, "/agents");
        var shown = await Command(runtime, parent, "/agents show 1");
        gate.SetResult();
        var finished = await ended.Task.WaitAsync(Ct);
        var after = await Command(runtime, parent, "/agents show 1");

        Assert.Equal(Corvid.Host.Commands.OutputTone.Success, output.Tone);
        Assert.Contains(output.Lines, line => line.Contains("generator fake-gen (local", StringComparison.Ordinal));
        var spawned = Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>());
        Assert.Equal((ChildSessions.ByUser, "look up the birthday of Ada", 3), (spawned.By, spawned.Goal, spawned.Budget.MaxSteps));
        Assert.Equal(["list_notes"], spawned.Tools);
        Assert.Equal("running", listed.Table!.Rows[0][2]);
        Assert.Contains(shown.Lines, line => line == "status: running");
        Assert.Equal(ChildStatuses.Answered, finished.Status);
        Assert.True(after.Lines.Contains("answer: Found three."), string.Join(" / ", after.Lines));
        Assert.Contains((await Command(runtime, parent, "/agents stop 1")).Lines, line => line.Contains("not running", StringComparison.Ordinal));
        Assert.Equal(Corvid.Host.Commands.OutputTone.Error, (await Command(runtime, parent, "/delegate --steps none go")).Tone);
        Assert.Contains("no model named", (await Command(runtime, parent, "/delegate --model generator=nope go")).Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_card_shows_hidden_characters_in_the_goal_and_model_names_revealed()
    {
        await using var host = Host(new FakeModels());
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var arguments = JsonSerializer.SerializeToElement(new { goal = "summarise‮ the notes", tools = new[] { "list_notes", "nope​" } });
        var prompt = new ApprovalPrompt("a-1", parent.Id, new ActionAnalysis("tool", "delegate_task", [], null), [], [ApprovalScope.Once],
            DateTimeOffset.UtcNow.AddMinutes(5))
        {
            Action = new ToolAction(runtime.Tools.List().Single(tool => tool.Name == DelegateTaskTool.ToolName), arguments),
        };

        var lines = await DelegationCard.LinesAsync(runtime, prompt, Ct);

        Assert.Equal("Child: summarise\\u202E the notes", lines[0]);
        Assert.Contains("  tools: list_notes", lines);
        Assert.Contains("  left out: nope\\u200B (the parent session has no such tool)", lines);
        Assert.Contains(lines, line => line.StartsWith("  budget: 8 steps, 64,000 tokens, 600 s", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("  models: ", StringComparison.Ordinal) && line.Contains("generator fake-gen", StringComparison.Ordinal));
        Assert.Contains("  approvals: manual", lines);
    }

    [Fact]
    public async Task A_childs_approval_reaches_the_parents_prompter_and_its_card_names_the_child()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "add a note", tools = new[] { "add_note" } })),
            Reply.Call("add_note", """{"text":"from the child"}"""),
            Reply.Say("Added."),
            Reply.Say("The helper added it.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var prompts = new Recording();
        var cards = new ConcurrentQueue<IReadOnlyList<string>>();
        using var attached = runtime.AttachPrompter(parent.Id, new CardReader(runtime, prompts, cards));

        await parent.SendAsync("ask a helper to do it", Ct);

        // The spawn's prompt, then the child's own call's, both answered by the parent's prompter.
        var childId = Events(runtime, parent.Id).OfType<ChildSpawned>().Single().ChildId;
        Assert.True(2 == prompts.Prompts.Count, string.Join("\n", Events(runtime, childId).Select(e => e.ToString()![..Math.Min(300, e.ToString()!.Length)])));
        var childPrompt = prompts.Prompts.Last();
        Assert.StartsWith(ChildSessions.Surface + "-", childPrompt.SessionId, StringComparison.Ordinal);
        Assert.Contains(cards.Last(), line => line.StartsWith($"Asked by child session {childPrompt.SessionId} of {parent.Id}", StringComparison.Ordinal));
        Assert.Contains("from the child", models.Tools.Notes);
    }

    /// <summary>Reads each prompt's card lines while it is open (the child still runs), then answers through another prompter.</summary>
    private sealed class CardReader(CorvidRuntime runtime, IApprovalPrompter inner, ConcurrentQueue<IReadOnlyList<string>> cards) : IApprovalPrompter
    {
        public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            cards.Enqueue(await DelegationCard.LinesAsync(runtime, prompt, cancellationToken));
            return await inner.AskAsync(prompt, cancellationToken);
        }
    }

    [Fact]
    public async Task The_delegate_question_is_asked_when_a_calibrated_decider_and_a_native_generator_could_use_it()
    {
        var models = new FakeModels();
        await using var host = Host(models, new()
        {
            ["Corvid:Models:Slots:decider:Model"] = "fake-kev",
            // fake-gen-large calls tools natively.
            ["Corvid:Models:Slots:generator:Model"] = "fake-gen-large",
            ["Corvid:Models:Slots:generator:Escalation:0"] = "fake-gen",
        });
        await using var runtime = await host.StartAsync();
        models.Kev.Answer = (key, question) => key == "delegate"
            ? [.. question.Options.Select(option => option.Name == "yes" ? 1.0 : 0.0)]
            : [.. question.Options.Select(_ => 1.0 / question.Options.Length)];
        var session = runtime.OpenSession();

        await session.SendAsync("research three things", Ct);

        var asked = Assert.Single(Events(runtime, session.Id).OfType<Decision>(), decision => decision.QuestionId == "delegate");
        Assert.Equal("delegate", asked.Action);
        Assert.Contains(DelegateTaskTool.ToolName, models.Larger.OfferedTools[0]);

        // Without the tool (here: turned off), the question is not asked.
        var quiet = new FakeModels();
        await using var offHost = Host(quiet, new()
        {
            ["Corvid:Tools:Delegate"] = "false",
            ["Corvid:Models:Slots:decider:Model"] = "fake-kev",
            ["Corvid:Models:Slots:generator:Model"] = "fake-gen-large",
            ["Corvid:Models:Slots:generator:Escalation:0"] = "fake-gen",
        });
        await using var off = await offHost.StartAsync();
        var plain = off.OpenSession();
        await plain.SendAsync("research three things", Ct);
        Assert.DoesNotContain(Events(off, plain.Id).OfType<Decision>(), decision => decision.QuestionId == "delegate");
    }
}
