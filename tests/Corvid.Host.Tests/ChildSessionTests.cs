using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>A5: child sessions (plan §14, delegate_task) in the host: plan, open, run, and the ceilings a model cannot raise.</summary>
public sealed class ChildSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolDescriptor Tool(string name, SideEffects effects) =>
        new(name, $"The {name} tool.", """{"type":"object","properties":{}}""", new ToolRisk(effects, Network: false, Destructive: false), []);

    /// <summary>Tools a child may never be given, and ask_user.</summary>
    private sealed class ForbiddenTools : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() =>
        [
            new AskUserTool(),
            new TestTool(Tool("save_fact", SideEffects.Memory), _ => "saved"),
            new TestTool(Tool("send_message", SideEffects.Messaging), _ => "sent"),
            new TestTool(Tool("cron_create", SideEffects.None), _ => "scheduled"),
            new TestTool(Tool(ChildSessions.DelegateToolName, SideEffects.None), _ => "delegated"),
        ];
    }

    /// <summary>Finds "Ada" as a person, so a gate set to refuse PII refuses a goal that names her.</summary>
    private sealed class PersonExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.Text.IndexOf("Ada", StringComparison.Ordinal);
            return Task.FromResult(new ExtractionResult(at < 0 || !request.Labels.Contains("person") ? []
                : [new ExtractedSpan("person", "Ada", at, at + 3, 0.99f)]));
        }
    }

    /// <summary>A clock whose timers fire only when the test says.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State)> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_timers)
                _timers.Add((callback, state));
            return new Timer();
        }

        public void Fire()
        {
            (TimerCallback Callback, object? State)[] due;
            lock (_timers)
                due = [.. _timers];
            foreach (var (callback, state) in due)
                callback(state);
        }

        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static TestHost Host(FakeModels models, Dictionary<string, string?>? settings = null, bool forbidden = false) =>
        TestHost.Create(models, settings, services =>
        {
            if (forbidden)
                services.AddSingleton<IToolProvider>(new ForbiddenTools());
        });

    private static IReadOnlyList<SessionEvent> Events(CorvidRuntime runtime, string sessionId) =>
        [.. runtime.Log.Read(sessionId, limit: 4096).Select(item => item.Payload)];

    [Fact]
    public async Task A_child_runs_its_goal_with_only_its_explicit_toolset_and_both_logs_say_so()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        await using var host = Host(models, forbidden: true);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var heard = new List<SessionStreamUpdate>();
        runtime.ChildChanged += (id, update) =>
        {
            lock (heard)
                heard.Add(update);
        };

        var plan = await runtime.PlanChildAsync(parent.Id, new ChildRequest("count my notes")
        {
            Tools = ["list_notes", "ask_user", "save_fact", "send_message", "cron_create", ChildSessions.DelegateToolName, "nope‮"],
        }, Ct);
        var child = runtime.OpenChildSession(plan);
        var result = await runtime.RunChildAsync(child, Ct);

        Assert.Equal(["list_notes"], plan.Tools);
        Assert.Equal(
            [
                ("ask_user", "a child cannot ask the person anything"),
                ("save_fact", "a child cannot write memory"),
                ("send_message", "a child cannot send messages"),
                ("cron_create", "a child cannot schedule jobs"),
                (ChildSessions.DelegateToolName, "a child cannot start children of its own"),
                ("nope\\u202E", "the parent session has no such tool"),
            ], plan.Dropped.Select(tool => (tool.Name, tool.Reason)));
        Assert.Equal((ChildStatuses.Answered, "You have two notes."), (result.Status, result.Text));
        // Only its toolset was offered to the child's generator.
        Assert.All(models.Generator.OfferedTools, offered => Assert.Equal(["list_notes"], offered));
        Assert.StartsWith(ChildSessions.Surface + "-", child.Id, StringComparison.Ordinal);
        Assert.Equal(1, child.Depth);
        Assert.Equal(parent.Id, child.ParentId);

        var parentEvents = Events(runtime, parent.Id);
        var spawned = Assert.Single(parentEvents.OfType<ChildSpawned>());
        Assert.Equal((child.Id, "count my notes", ChildSessions.ByAgent, false), (spawned.ChildId, spawned.Goal, spawned.By, spawned.Tainted));
        var finished = Assert.Single(parentEvents.OfType<ChildFinished>());
        Assert.Equal((ChildStatuses.Answered, 1, 2L * 49), (finished.Status, finished.Steps, finished.Tokens));
        var childEvents = Events(runtime, child.Id);
        var origin = Assert.IsType<ChildOf>(childEvents[0]);
        Assert.Equal((parent.Id, 1), (origin.ParentId, origin.Depth));
        Assert.Equal(parent.Id, SessionProjectionOf(runtime, child.Id));
        // The child's decisions are in its log, for calibration as any session's.
        Assert.NotEmpty(childEvents.OfType<Decision>());
        // Let go of when done: its driver lock and actor.
        Assert.False(runtime.TryGetSession(child.Id, out _));
        Assert.Collection(heard, update => Assert.IsType<ChildStarted>(update), update => Assert.IsType<ChildEnded>(update));
    }

    private static string? SessionProjectionOf(CorvidRuntime runtime, string sessionId) =>
        Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, sessionId).Parent?.ParentId;

    [Fact]
    public async Task A_request_cannot_raise_the_ceilings()
    {
        await using var host = Host(new FakeModels(), new()
        {
            ["Corvid:Agents:MaxSteps"] = "6",
            ["Corvid:Agents:MaxTokens"] = "5000",
            ["Corvid:Agents:MaxSeconds"] = "60",
            ["Corvid:Turn:MaxSteps"] = "4",
        });
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        parent.ApprovalMode = ApprovalMode.Smart;

        var plan = await runtime.PlanChildAsync(parent.Id, new ChildRequest("go")
        {
            MaxSteps = 99,
            MaxTokens = 1_000_000,
            MaxSeconds = 86_400,
            ApprovalMode = ApprovalMode.Permissive,
        }, Ct);
        var smaller = await runtime.PlanChildAsync(parent.Id, new ChildRequest("go") { MaxSteps = 2, MaxTokens = 100, MaxSeconds = 5 }, Ct);

        Assert.Equal(new ChildBudget(4, 5_000, 60), plan.Budget);
        Assert.Equal(ApprovalMode.Smart, plan.ApprovalMode);
        Assert.Equal(new ChildBudget(2, 100, 5), smaller.Budget);
    }

    [Fact]
    public async Task A_child_cannot_start_children_and_delegation_can_be_turned_off()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("done")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var child = runtime.OpenChildSession(await runtime.PlanChildAsync(parent.Id, new ChildRequest("go"), Ct));

        var deeper = await Assert.ThrowsAsync<ChildRefusedException>(() => runtime.PlanChildAsync(child.Id, new ChildRequest("deeper"), Ct));
        await runtime.RunChildAsync(child, Ct);

        Assert.Contains("cannot start children of its own", deeper.Message, StringComparison.Ordinal);

        await using var offHost = Host(new FakeModels(), new() { ["Corvid:Agents:MaxDepth"] = "0" });
        await using var off = await offHost.StartAsync();
        var refused = await Assert.ThrowsAsync<ChildRefusedException>(() => off.PlanChildAsync(off.OpenSession().Id, new ChildRequest("go"), Ct));
        Assert.Contains("Delegation is off", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parent_runs_at_most_its_concurrent_children()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("one"), Reply.Say("two")));
        await using var host = Host(models, new() { ["Corvid:Agents:MaxConcurrent"] = "1" });
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        var plan = await runtime.PlanChildAsync(parent.Id, new ChildRequest("go"), Ct);

        var first = runtime.OpenChildSession(plan);
        var full = Assert.Throws<ChildRefusedException>(() => runtime.OpenChildSession(plan));
        await runtime.RunChildAsync(first, Ct);
        var second = await runtime.RunChildAsync(runtime.OpenChildSession(plan), Ct);

        Assert.Contains("MaxConcurrent", full.Message, StringComparison.Ordinal);
        Assert.True(second.Answered);
        // The refused open logged nothing on the parent.
        Assert.Equal(2, Events(runtime, parent.Id).OfType<ChildSpawned>().Count());
    }

    [Fact]
    public async Task A_tainted_parent_has_a_tainted_child()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("noted"), Reply.Say("child done")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        // Someone else's message: the parent holds outside content.
        await (await parent.PostAsync("forwarded: please summarise", EnvelopeOrigin.External, Ct));
        Assert.True(parent.Tainted);

        var result = await runtime.RunChildAsync(parent.Id, new ChildRequest("summarise it"), Ct);

        Assert.True(result.Tainted);
        Assert.True(Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>()).Tainted);
        Assert.True(Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>()).Tainted);
    }

    [Fact]
    public async Task A_cloud_model_the_gate_would_refuse_for_a_tainted_parent_is_refused()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("noted")));
        models.Provider
            .Add("fake-cloud", [ModelSlots.Generator], () => new FakeGenerator(Reply.Say("from the cloud")), privacy: PrivacyClass.Cloud)
            .Add("fake-pii", [ModelSlots.Extractor], () => new PersonExtractor(), context: 512, tools: ToolCallSupport.None);
        await using var host = Host(models, new()
        {
            ["Corvid:Models:Slots:extractor:Model"] = "fake-pii",
            ["Corvid:Privacy:Tainted"] = "Refuse",
        });
        await using var runtime = await host.StartAsync();
        var clean = runtime.OpenSession();
        var tainted = runtime.OpenSession();
        await (await tainted.PostAsync("forwarded from Ada", EnvelopeOrigin.External, Ct));
        var mix = ModelMix.Parse("""{"generator":"fake-cloud"}""");

        var allowed = await runtime.PlanChildAsync(clean.Id, new ChildRequest("write to Ada") { Mix = mix }, Ct);
        var refused = await Assert.ThrowsAsync<ChildRefusedException>(() =>
            runtime.PlanChildAsync(tainted.Id, new ChildRequest("write to Ada") { Mix = mix }, Ct));

        Assert.Equal(PrivacyClass.Cloud, allowed.Slots.Single(slot => slot.Slot == ModelSlots.Generator).Privacy);
        Assert.Contains("privacy gate would refuse fake-cloud", refused.Message, StringComparison.Ordinal);
        Assert.Contains("outside content", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Events(runtime, tainted.Id).OfType<ChildSpawned>());
    }

    /// <summary>Finds "Ada" and, as the real extractor did on the e2e pass, the words "child session" as people.</summary>
    private sealed class EagerPersonExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var spans = new List<ExtractedSpan>();
            foreach (var name in new[] { "Ada", "child session" })
                for (var at = request.Text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = request.Text.IndexOf(name, at + 1, StringComparison.Ordinal))
                    spans.Add(new ExtractedSpan("person", name, at, at + name.Length, 0.99f));
            return Task.FromResult(new ExtractionResult(request.Labels.Contains("person") ? [.. spans] : []));
        }
    }

    [Fact]
    public async Task A_gated_child_model_gets_the_child_prompt_as_written_and_the_goal_redacted()
    {
        var cloud = new FakeGenerator(Reply.Say("""{"to":"her"}"""));
        var models = new FakeModels(new FakeGenerator(Reply.Say("noted")));
        models.Provider
            .Add("fake-cloud", [ModelSlots.Generator], () => cloud, privacy: PrivacyClass.Cloud)
            .Add("fake-pii", [ModelSlots.Extractor], () => new EagerPersonExtractor(), context: 512, tools: ToolCallSupport.None);
        await using var host = Host(models, new() { ["Corvid:Models:Slots:extractor:Model"] = "fake-pii" });
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();

        var result = await runtime.RunChildAsync(parent.Id, new ChildRequest("write to Ada")
        {
            Mix = ModelMix.Parse("""{"generator":"fake-cloud"}"""),
            OutputSchema = JsonDocument.Parse("""{"type":"object","properties":{"to":{"type":"string","description":"Ada"}}}""").RootElement,
        }, Ct);

        var sent = Assert.Single(cloud.Calls);
        var system = string.Join("\n", sent.Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.System).Select(message => message.Text));
        // Corvid's own words reach the model as written; the schema after them is the model's and is still read.
        Assert.Contains(CorvidRuntime.ChildSystemPrompt + CorvidRuntime.ChildSchemaPrompt, system, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada", system, StringComparison.Ordinal);
        var user = sent.Last(message => message.Role == Microsoft.Extensions.AI.ChatRole.User).Text;
        Assert.DoesNotContain("Ada", user, StringComparison.Ordinal);
        Assert.Contains("<PERSON_", user, StringComparison.Ordinal);
        Assert.Equal(ChildStatuses.Answered, result.Status);
    }

    [Fact]
    public async Task A_mix_runs_the_child_on_its_models_and_the_allowlist_bounds_it()
    {
        var models = new FakeModels();
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();

        var result = await runtime.RunChildAsync(parent.Id, new ChildRequest("say hello")
        {
            Mix = ModelMix.Parse("""{"generator":{"model":"FAKE-GEN-LARGE","escalate":false}}"""),
        }, Ct);

        Assert.Equal("Hello from the larger model.", result.Text);
        var spawned = Assert.Single(Events(runtime, parent.Id).OfType<ChildSpawned>());
        Assert.Equal("""{"generator":{"model":"fake-gen-large","ladder":[],"escalate":false}}""", spawned.Mix!.Value.GetRawText());
        // The parent is untouched.
        Assert.Equal("fake-gen@cpu", parent.Slots.Current(ModelSlots.Generator).Profile.Id);

        await using var boundedHost = Host(new FakeModels(), new() { ["Corvid:Agents:Models:0"] = "fake-gen" });
        await using var bounded = await boundedHost.StartAsync();
        var refused = await Assert.ThrowsAsync<ChildRefusedException>(() => bounded.PlanChildAsync(bounded.OpenSession().Id,
            new ChildRequest("go") { Mix = ModelMix.Parse("""{"generator":"fake-gen-large"}""") }, Ct));
        Assert.Contains("not in Corvid:Agents:Models", refused.Message, StringComparison.Ordinal);
        var unknown = await Assert.ThrowsAsync<ChildRefusedException>(() => bounded.PlanChildAsync(bounded.OpenSession().Id,
            new ChildRequest("go") { Mix = ModelMix.Parse("""{"generator":"gpt-9"}""") }, Ct));
        Assert.Contains("no model named \"gpt-9\"", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_mix_from_configuration_is_used_under_a_passed_one()
    {
        await using var host = Host(new FakeModels(), new()
        {
            ["Corvid:Agents:Mixes:big:generator:Model"] = "fake-gen-large",
            ["Corvid:Agents:Mixes:big:generator:Escalate"] = "false",
        });
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();

        var named = await runtime.PlanChildAsync(parent.Id, new ChildRequest("go") { MixName = "big" }, Ct);
        var over = await runtime.PlanChildAsync(parent.Id, new ChildRequest("go")
        {
            MixName = "big",
            Mix = ModelMix.Parse("""{"generator":"fake-gen"}"""),
        }, Ct);
        var missing = await Assert.ThrowsAsync<ChildRefusedException>(() => runtime.PlanChildAsync(parent.Id, new ChildRequest("go") { MixName = "small" }, Ct));

        Assert.Equal(("fake-gen-large", false), (named.Slots.Single(slot => slot.Slot == ModelSlots.Generator).Model, named.Slots.Single(slot => slot.Slot == ModelSlots.Generator).Escalate));
        Assert.Equal("fake-gen", over.Slots.Single(slot => slot.Slot == ModelSlots.Generator).Model);
        Assert.Contains("No mix is named \"small\"", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_childs_approvals_go_to_its_parents_prompter_under_the_parents_ceiling()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"from the child"}"""), Reply.Say("Added.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        parent.ApprovalMode = ApprovalMode.Manual;
        var prompter = new CompositionTests.RecordingPrompter(ApprovalAnswer.Approve());
        using var attached = runtime.AttachPrompter(parent.Id, prompter);

        var plan = await runtime.PlanChildAsync(parent.Id, new ChildRequest("add a note") { Tools = ["add_note"], ApprovalMode = ApprovalMode.Permissive }, Ct);
        var child = runtime.OpenChildSession(plan);
        child.ApprovalMode = ApprovalMode.Permissive;
        var mode = child.ApprovalMode;
        var result = await runtime.RunChildAsync(child, Ct);

        Assert.Equal((ApprovalMode.Manual, ApprovalMode.Manual), (plan.ApprovalMode, mode));
        var prompt = Assert.Single(prompter.Prompts);
        Assert.Equal(child.Id, prompt.SessionId);
        Assert.Contains("from the child", models.Tools.Notes);
        Assert.True(result.Answered);
    }

    [Fact]
    public async Task An_answer_is_checked_against_the_output_schema()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("```json\n{\"count\": 2}\n```"), Reply.Say("two of them")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"]}""");

        var good = await runtime.RunChildAsync(parent.Id, new ChildRequest("count") { OutputSchema = schema.RootElement }, Ct);
        var bad = await runtime.RunChildAsync(parent.Id, new ChildRequest("count") { OutputSchema = schema.RootElement }, Ct);
        var unsupported = await Assert.ThrowsAsync<ChildRefusedException>(() => runtime.PlanChildAsync(parent.Id,
            new ChildRequest("count") { OutputSchema = JsonDocument.Parse("""{"type":"array"}""").RootElement }, Ct));

        Assert.Equal((ChildStatuses.Answered, 2), (good.Status, good.Output!.Value.GetProperty("count").GetInt32()));
        Assert.Equal(ChildStatuses.InvalidOutput, bad.Status);
        Assert.Contains("no JSON object", bad.Detail, StringComparison.Ordinal);
        Assert.Equal([true, false], Events(runtime, parent.Id).OfType<ChildFinished>().Select(finished => finished.OutputValid));
        Assert.Contains("describes an object", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_past_its_token_budget_is_stopped()
    {
        // Each fake call reports 49 tokens: the second one passes a budget of 60.
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Call("list_notes"), Reply.Say("never")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();

        var result = await runtime.RunChildAsync(parent.Id, new ChildRequest("list") { Tools = ["list_notes"], MaxTokens = 60 }, Ct);

        Assert.Equal(ChildStatuses.TokenBudgetExhausted, result.Status);
        Assert.Equal(98, result.Tokens);
        Assert.Contains("of 60", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_past_its_wall_time_is_stopped()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(Reply.Say("late"))
        {
            Hold = (_, token) =>
            {
                started.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = Host(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var clock = new ManualClock();
        runtime.ChildClock = clock;
        var parent = runtime.OpenSession();

        var running = runtime.RunChildAsync(parent.Id, new ChildRequest("wait") { MaxSeconds = 30 }, Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        clock.Fire();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(ChildStatuses.TimedOut, result.Status);
        Assert.Equal("ran past 30 s", result.Detail);
    }

    [Fact]
    public async Task Cancelling_the_parents_wait_cancels_the_child()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(Reply.Say("late"))
        {
            Hold = (_, token) =>
            {
                started.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = Host(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var running = runtime.RunChildAsync(parent.Id, new ChildRequest("wait"), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await cancel.CancelAsync();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(ChildStatuses.Cancelled, result.Status);
        Assert.Equal(ChildStatuses.Cancelled, Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>()).Status);
    }

    [Fact]
    public async Task A_child_never_ends_on_a_question()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("ask_user", """{"question":"Which one?"}"""), Reply.Say("Picked the first.")));
        await using var host = Host(models, forbidden: true);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();

        // Even asked for, ask_user is not offered, and a call to it is refused as not offered.
        var result = await runtime.RunChildAsync(parent.Id, new ChildRequest("pick one") { Tools = ["ask_user", "list_notes"] }, Ct);

        Assert.Equal(ChildStatuses.Answered, result.Status);
        Assert.Empty(Events(runtime, result.ChildId).OfType<ClarificationAsked>());
    }
}
