using System.Collections.Concurrent;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// A5, where the phase's groups meet: a child session on the model the person switched its parent to, a fork of a session
/// with a session choice, a delegation inside a fork (its taint and its choices), and the tokens a child's turn used.
/// </summary>
public sealed class A5MeetingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Approves every prompt and keeps it.</summary>
    private sealed class Approving : IApprovalPrompter
    {
        public ConcurrentQueue<ApprovalPrompt> Prompts { get; } = new();

        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return ValueTask.FromResult(ApprovalAnswer.Approve());
        }
    }

    /// <summary>Finds "Ada" as a person, so the privacy gate has something to refuse.</summary>
    private sealed class PersonExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.Text.IndexOf("Ada", StringComparison.Ordinal);
            return Task.FromResult(new ExtractionResult(at < 0 || !request.Labels.Contains("person") ? []
                : [new ExtractedSpan("person", "Ada", at, at + 3, 0.99f)]));
        }
    }

    /// <summary>The fake models plus a second local generator, <c>fake-gen-b</c>, with its own script.</summary>
    private static FakeModels Models(FakeGenerator b, FakeGenerator? main = null)
    {
        var models = new FakeModels(main ?? new FakeGenerator(Reply.Say("from the default generator")));
        models.Provider.Add("fake-gen-b", [ModelSlots.Generator], () => b, tools: ToolCallSupport.Native);
        return models;
    }

    private static TestHost Host(FakeModels models, Dictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Corvid:Tools:Delegate"] = "true",
            ["Corvid:Approvals:Mode"] = "Manual",
        };
        foreach (var (key, value) in settings ?? [])
            all[key] = value;
        return TestHost.Create(models, all);
    }

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static IReadOnlyList<SessionEvent> Events(CorvidRuntime runtime, string sessionId) =>
        [.. runtime.Log.Read(sessionId, limit: 4096, cancellationToken: Ct).Select(item => item.Payload)];

    private static JsonElement ResultOf(CorvidRuntime runtime, string sessionId) =>
        Events(runtime, sessionId).OfType<ToolResult>().Last().Result;

    [Fact]
    public async Task A_child_runs_on_the_model_the_person_switched_its_parent_to_unless_its_mix_names_another()
    {
        var b = new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count my notes", tools = new[] { "list_notes" } })),
            Reply.Call("list_notes"),
            Reply.Say("child on b"),
            Reply.Say("Your helper on b answered."),
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count again", tools = new[] { "list_notes" }, mix = new { generator = new { model = "fake-gen-large" } } })),
            Reply.Say("Your helper on the larger model answered."));
        var models = Models(b);
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Approving());
        var chosen = await parent.SetModelAsync(ModelSlots.Generator, "fake-gen-b", SwitchScope.Session, Ct);
        Assert.Equal(SwitchStatus.Switched, chosen.Status);

        Assert.Equal("Your helper on b answered.", (await parent.SendAsync("ask a helper", Ct)).Result!.Text);
        // No mix: the child's generator is the parent's session choice (rebound at subagent priority), not the registry's.
        Assert.Equal("child on b", ResultOf(runtime, parent.Id).GetProperty("answer").GetString());
        var first = Events(runtime, parent.Id).OfType<ChildSpawned>().Single();
        Assert.Null(first.Mix);

        // A mix naming another generator wins over the parent's choice, for the child only.
        Assert.Equal("Your helper on the larger model answered.", (await parent.SendAsync("ask another", Ct)).Result!.Text);
        Assert.Equal("Hello from the larger model.", ResultOf(runtime, parent.Id).GetProperty("answer").GetString());
        Assert.Equal("fake-gen-b@cpu", parent.Slots.Current(ModelSlots.Generator).Profile.Id);
        Assert.True(parent.Slots.HasSessionChoice(ModelSlots.Generator));
        Assert.Equal("fake-gen-large", Events(runtime, parent.Id).OfType<ChildSpawned>().Last().Mix!.Value.GetProperty("generator")
            .GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_fork_of_a_session_with_a_session_choice_keeps_it_even_when_the_source_is_not_open()
    {
        var b = new FakeGenerator(Reply.Say("noted on b"), Reply.Say("the fork on b"));
        await using var host = Host(Models(b));
        string sourceId;
        await using (var first = await host.StartAsync())
        {
            var source = first.OpenSession();
            sourceId = source.Id;
            Assert.Equal(SwitchStatus.Switched, (await source.SetModelAsync(ModelSlots.Generator, "fake-gen-b", SwitchScope.Session, Ct)).Status);
            Assert.Equal("noted on b", (await source.SendAsync("note this", Ct)).Result!.Text);
        }

        // A new runtime: the source is not open here, so the fork reads its choices from the copied log alone.
        await using var second = await host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Ct);
        Assert.False(second.TryGetSession(sourceId, out _));
        var fork = await second.ForkAsync(sourceId, new ForkOptions { Surface = "test" }, Ct);

        Assert.False(fork.DuringTurn);
        Assert.True(fork.Session.Slots.HasSessionChoice(ModelSlots.Generator));
        Assert.Equal("fake-gen-b@cpu", fork.Session.Slots.Current(ModelSlots.Generator).Profile.Id);
        Assert.Equal("the fork on b", (await fork.Session.SendAsync("go on", Ct)).Result!.Text);
        // The turn's end returns to the choice, not to the registry's default.
        Assert.Equal("fake-gen-b@cpu", fork.Session.Slots.Current(ModelSlots.Generator).Profile.Id);
        Assert.Equal("fake-gen-b@cpu", second.ModelChoices(fork.Session.Id)[ModelSlots.Generator]);
    }

    [Fact]
    public async Task A_delegation_inside_a_fork_of_a_tainted_session_inherits_its_taint_and_runs_on_the_forks_choice()
    {
        var b = new FakeGenerator(
            Reply.Say("noted on b"),
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count my notes", tools = new[] { "list_notes" } })),
            Reply.Call("list_notes"),
            Reply.Say("child of the fork"),
            Reply.Say("The fork's helper answered."));
        var models = Models(b);
        models.Provider
            .Add("fake-cloud", [ModelSlots.Generator], () => new FakeGenerator(Reply.Say("from the cloud")), privacy: PrivacyClass.Cloud)
            .Add("fake-pii", [ModelSlots.Extractor], () => new PersonExtractor(), context: 512, tools: ToolCallSupport.None);
        await using var host = Host(models, new()
        {
            ["Corvid:Models:Slots:extractor:Model"] = "fake-pii",
            ["Corvid:Privacy:Tainted"] = "Refuse",
        });
        await using var runtime = await host.StartAsync();
        var source = runtime.OpenSession();
        Assert.Equal(SwitchStatus.Switched, (await source.SetModelAsync(ModelSlots.Generator, "fake-gen-b", SwitchScope.Session, Ct)).Status);
        await (await source.PostAsync("forwarded from someone", EnvelopeOrigin.External, Ct));
        Assert.True(source.Tainted);

        var fork = (await runtime.ForkAsync(source.Id, new ForkOptions { Surface = "test" }, Ct)).Session;
        using var attached = runtime.AttachPrompter(fork.Id, new Approving());
        Assert.True(fork.Tainted);

        // Before the person writes in it, the fork holds its source's outside content: a child planned now inherits that
        // taint, and a cloud model for it is refused at the gate, as for its parent.
        Assert.True((await runtime.PlanChildAsync(fork.Id, new ChildRequest("count my notes"), Ct)).Tainted);
        var refused = await Assert.ThrowsAsync<ChildRefusedException>(() => runtime.PlanChildAsync(fork.Id,
            new ChildRequest("write to Ada") { Mix = ModelMix.Parse("""{"generator":"fake-cloud"}""") }, Ct));
        Assert.Contains("privacy gate would refuse fake-cloud", refused.Message, StringComparison.Ordinal);
        Assert.Contains("outside content", refused.Message, StringComparison.Ordinal);

        // The person's message clears the taint for the turn (plan §10.3); the agent's child then runs on the fork's choice.
        Assert.Equal("The fork's helper answered.", (await fork.SendAsync("ask a helper", Ct)).Result!.Text);
        var spawned = Assert.Single(Events(runtime, fork.Id).OfType<ChildSpawned>());
        Assert.Equal(spawned.Tainted, Assert.Single(Events(runtime, fork.Id).OfType<ChildFinished>()).Tainted);
        Assert.Equal("child of the fork", ResultOf(runtime, fork.Id).GetProperty("answer").GetString());
        Assert.Contains(runtime.Children(fork.Id), child => child.ChildId == spawned.ChildId);
        Assert.Empty(runtime.Children(source.Id));
        Assert.Empty(Events(runtime, source.Id).OfType<ChildSpawned>());
        // The child's log is its parent's record: it cannot be forked.
        await Assert.ThrowsAsync<Corvid.Kernel.Sessions.SessionForkException>(() => runtime.ForkAsync(spawned.ChildId, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_childs_token_use_is_in_its_log_and_its_finish_counts_the_same()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call(DelegateTaskTool.ToolName, Args(new { goal = "count my notes", tools = new[] { "list_notes" } })),
            Reply.Call("list_notes"),
            Reply.Say("You have two notes."),
            Reply.Say("Your helper says two.")));
        await using var host = Host(models);
        await using var runtime = await host.StartAsync();
        var parent = runtime.OpenSession();
        using var attached = runtime.AttachPrompter(parent.Id, new Approving());

        await parent.SendAsync("ask a helper", Ct);

        var finished = Assert.Single(Events(runtime, parent.Id).OfType<ChildFinished>());
        var child = Events(runtime, finished.ChildId);
        var usage = child.OfType<ToolCallIssued>().Select(call => call.Usage).Concat(child.OfType<AssistantMessage>().Select(answer => answer.Usage))
            .OfType<TokenUsage>().ToList();
        // Two generator responses in the child (the call, then the answer), each 42 in and 7 out (FakeGenerator).
        Assert.Equal(2, usage.Sum(item => item.Calls));
        Assert.Equal(2 * (42 + 7), usage.Sum(item => (item.InputTokens ?? 0) + (item.OutputTokens ?? 0)));
        Assert.Equal(2 * (42 + 7), finished.Tokens);
        // The child's call says which generator asked for it.
        Assert.Equal("fake-gen@cpu", Assert.Single(child.OfType<ToolCallIssued>()).Generator);
        // The parent's own record of its turn is apart from the child's: its delegate call and answer carry its own use.
        var own = Events(runtime, parent.Id);
        Assert.NotNull(own.OfType<ToolCallIssued>().Single(call => call.ToolName == DelegateTaskTool.ToolName).Usage);
        Assert.NotNull(own.OfType<AssistantMessage>().Last().Usage);
    }
}
