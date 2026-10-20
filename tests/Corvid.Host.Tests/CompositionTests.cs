using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

public sealed class CompositionTests
{
    [Fact]
    public async Task SlotsAreComposedFromConfiguration()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();

        Assert.Equal("fake-gen@cpu", runtime.Registry.Get(ModelSlots.Generator).Default.Profile.Id);
        Assert.Equal(["fake-gen-large@cpu"], runtime.Registry.Get(ModelSlots.Generator).Escalation.Select(binding => binding.Profile.Id));
        Assert.Equal(["fake-embed-alt@cpu"], runtime.Registry.Get(ModelSlots.TextEmbedder).Fallbacks.Select(binding => binding.Profile.Id));
        Assert.Equal("fake-extract@cpu", runtime.Registry.Get(ModelSlots.Extractor).Default.Profile.Id);
        // Each model loads once, however many wrappings (turn, decider, memory) it has.
        Assert.Equal(host.Models.Provider.Loaded.Count, host.Models.Provider.Loaded.Distinct().Count());
        // The CPU device scheduler exists for the bindings placed there.
        Assert.True(runtime.Devices.TryGet("cpu", out _));
    }

    [Fact]
    public async Task MissingModelsAreReportedTogetherBeforeAnythingLoads()
    {
        var models = new FakeModels(generatorReady: false);
        await using var host = TestHost.Create(models);
        await host.Host.StartAsync(TestContext.Current.CancellationToken);
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();

        var exception = await Assert.ThrowsAsync<ModelsUnavailableException>(() => factory.StartAsync(TestContext.Current.CancellationToken));
        var missing = Assert.Single(exception.Missing);
        Assert.Equal("fake-gen", missing.Model.Name);
        Assert.Equal("corvid model download fake-gen", missing.Acquire);
        Assert.Empty(models.Provider.Loaded);
    }

    [Fact]
    public async Task ToolsRegisteredInDependencyInjectionAreOffered()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        // The skill tools (C-017) come with skills, which are on by default.
        Assert.Equal(["add_note", "list_notes", "skill_read", "skill_run_script"], runtime.Tools.List().Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheSkillsDirectoryIsReadOnlyToToolsWhenTheWorkspaceContainsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var skills = Path.Combine(root, "data", "corvid-skills");
            var options = new Configuration.CorvidOptions { Skills = new Configuration.SkillsSettings { Directory = skills } };

            // A workspace above the skills directory (the home directory, say): commands may read skills, not change them.
            var (readOnly, refusal) = CorvidRuntimeFactory.SkillsGuard(options, root);
            Assert.Equal([skills], readOnly);
            Assert.Null(refusal);
            Assert.True(Directory.Exists(skills));

            // A workspace elsewhere needs nothing more: the sandbox leaves everything outside it read-only.
            Assert.Empty(CorvidRuntimeFactory.SkillsGuard(options, Path.Combine(root, "work")).ReadOnly);

            // A workspace in the skills directory would make every tool write a skill edit: skills are off.
            Assert.Contains("inside the skills directory", CorvidRuntimeFactory.SkillsGuard(options, skills).Refusal);
            Assert.NotNull(CorvidRuntimeFactory.SkillsGuard(options, Path.Combine(skills, "devops")).Refusal);

            options.Skills.Enabled = false;
            Assert.Equal((ImmutableArray<string>.Empty, (string?)null), CorvidRuntimeFactory.SkillsGuard(options, root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AScriptedTurnRunsThroughTheComposedRuntime()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        var delivery = await session.SendAsync("list my notes", TestContext.Current.CancellationToken);

        Assert.Null(delivery.Error);
        Assert.Equal(TurnStatus.Answered, delivery.Result!.Status);
        Assert.Equal("You have two notes.", delivery.Result.Text);
        Assert.Equal(1, models.Tools.List.Invocations);
        var events = session.Snapshot().Transcript.Select(entry => entry.Content).ToList();
        Assert.Contains(events, item => item is ToolCallIssued { ToolName: "list_notes" });
        Assert.Contains(events, item => item is AssistantMessage { Text: "You have two notes." });
        // The degraded decider answered every checkpoint, so its latency is on record for /status.
        Assert.True(runtime.Metrics.TryGet(ModelSlots.Decider, "degraded", out var decider) && decider.Calls > 0);
        Assert.True(runtime.Metrics.TryGet(ModelSlots.Generator, "fake-gen@cpu", out var generator) && generator.Calls == 2);
    }

    [Fact]
    public async Task TheEpisodicIndexCatchesUpAfterEachTurn()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        await session.SendAsync("remember that the boat is blue", TestContext.Current.CancellationToken);
        await runtime.MaintenanceIdleAsync();

        Assert.True(runtime.Memory!.Episodes.IndexedThrough(session.Id) > 0);
        Assert.Contains(runtime.Memory.Episodes.Search("boat", sessionId: session.Id), hit => hit.Episode.Text.Contains("boat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionCanBeResumedById()
    {
        await using var host = TestHost.Create();
        string id;
        await using (var runtime = await host.StartAsync())
        {
            var session = runtime.OpenSession();
            id = session.Id;
            await session.SendAsync("hello", TestContext.Current.CancellationToken);
        }
        await using (var runtime = await host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(TestContext.Current.CancellationToken))
        {
            var resumed = runtime.OpenSession(id);
            Assert.Contains(resumed.Snapshot().Transcript, entry => entry.Content is UserMessage { Text: "hello" });
        }
    }

    [Fact]
    public async Task ApprovalsGoToTheAttachedSurfaceAndFailWithoutOne()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added."),
            Reply.Call("add_note", """{"text":"feed cat"}"""), Reply.Say("Not added.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        var prompter = new RecordingPrompter(Corvid.Approvals.ApprovalAnswer.Approve());
        using (runtime.AttachPrompter(session.Id, prompter))
            await session.SendAsync("add a note: water plants", TestContext.Current.CancellationToken);
        Assert.Single(prompter.Prompts);
        Assert.Contains("water plants", models.Tools.Notes);

        // No surface attached: the prompt fails and the call is denied, never approved on the user's behalf.
        await session.SendAsync("add a note: feed cat", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("feed cat", models.Tools.Notes);
        Assert.Contains(session.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolResult>(), result => result.IsError);
    }

    [Fact]
    public async Task TheTurnFeedStreamsTextAndReportsTools()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var observer = new RecordingObserver();
        using (runtime.Feed.Subscribe(session.Id, observer))
            await session.SendAsync("list my notes", TestContext.Current.CancellationToken);

        Assert.Equal("You have two notes.", string.Concat(observer.Deltas));
        Assert.True(observer.Deltas.Count > 1);
        Assert.Equal(["started list_notes", "finished list_notes succeeded"], observer.Tools);
        Assert.Contains(observer.Checkpoints, checkpoint => checkpoint.Checkpoint == "ingress");
        // The turn's generator streamed; the degraded decider's yes/no calls (on their own wrapping) did not stream.
        Assert.True(models.Generator.StreamedCalls >= 2);
    }

    [Fact]
    public async Task ACompactionInAWatchedSessionStreamsOnlyTheAnswer()
    {
        // No summarizer is configured, so the generator's binding writes the records (C-016); its replies are records,
        // not answers, and must not reach the surface.
        const string record = """{"goal":"read the history","decisions":["read it"],"next_step":"answer"}""";
        var models = new FakeModels(new FakeGenerator([.. Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(
            messages => Reply.Text(messages[0].Text == Corvid.Kernel.Context.ContextCompactor.Instructions ? record : "All caught up."), 6)]));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var history = string.Concat(Enumerable.Repeat("a long earlier answer, ", 1_400));
        runtime.Log.Append(session.Id, 0, [EventData.Create(new UserMessage("tell me everything")), EventData.Create(new AssistantMessage(history))],
            TestContext.Current.CancellationToken);
        var observer = new RecordingObserver();
        using (runtime.Feed.Subscribe(session.Id, observer))
            await session.SendAsync("and now?", TestContext.Current.CancellationToken);

        Assert.Equal("All caught up.", string.Concat(observer.Deltas));
        Assert.Contains(session.Snapshot().Transcript, entry => entry.Content is Compacted);
    }

    [Fact]
    public async Task AConfiguredSummarizerSlotWritesTheRecords()
    {
        const string record = """{"goal":"read the history","decisions":["read it"],"next_step":"answer"}""";
        var models = new FakeModels(new FakeGenerator([.. Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(
            messages => Reply.Text(messages[0].Text == Corvid.Kernel.Context.ContextCompactor.Instructions ? record : "All caught up."), 6)]));
        await using var host = TestHost.Create(models, new Dictionary<string, string?> { ["Corvid:Models:Slots:summarizer:Model"] = "fake-gen" });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var history = string.Concat(Enumerable.Repeat("a long earlier answer, ", 1_400));
        runtime.Log.Append(session.Id, 0, [EventData.Create(new UserMessage("tell me everything")), EventData.Create(new AssistantMessage(history))],
            TestContext.Current.CancellationToken);

        var delivery = await session.SendAsync("and now?", TestContext.Current.CancellationToken);

        Assert.True(runtime.Registry.TryGet(ModelSlots.Summarizer, out _));
        Assert.Equal("All caught up.", delivery.Result!.Text);
        var compacted = Assert.IsType<Compacted>(session.Snapshot().Transcript[0].Content);
        Assert.Equal("fake-gen@cpu", compacted.Summarizer);
        Assert.Equal(["read it"], compacted.Summary.Decisions);
    }

    [Fact]
    public async Task NoObserverMeansNoStreaming()
    {
        var models = new FakeModels();
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var delivery = await runtime.OpenSession().SendAsync("hi", TestContext.Current.CancellationToken);
        Assert.Equal("Hello from the fake generator.", delivery.Result!.Text);
        Assert.Equal(0, models.Generator.StreamedCalls);
    }

    [Fact]
    public void LatencyIsTheMedianOfRecentCalls()
    {
        var latency = new ModelLatency();
        Assert.Null(latency.P50);
        foreach (var ms in new[] { 10, 30, 20, 1000 })
            latency.Record(TimeSpan.FromMilliseconds(ms), failed: ms == 1000);
        Assert.Equal(TimeSpan.FromMilliseconds(25), latency.P50);
        Assert.Equal(4, latency.Calls);
        Assert.Equal(1, latency.Failures);
        for (var i = 0; i < 200; i++)
            latency.Record(TimeSpan.FromMilliseconds(5), failed: false);
        Assert.Equal(TimeSpan.FromMilliseconds(5), latency.P50);
    }

    internal sealed class RecordingPrompter(Corvid.Approvals.ApprovalAnswer answer) : Corvid.Approvals.IApprovalPrompter
    {
        public List<Corvid.Approvals.ApprovalPrompt> Prompts { get; } = [];

        public ValueTask<Corvid.Approvals.ApprovalAnswer> AskAsync(Corvid.Approvals.ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            return ValueTask.FromResult(answer);
        }
    }

    internal sealed class RecordingObserver : ITurnObserver
    {
        public List<string> Deltas { get; } = [];

        public List<string> Tools { get; } = [];

        public List<CheckpointDecisions> Checkpoints { get; } = [];

        public void Text(string delta)
        {
            lock (Deltas)
                Deltas.Add(delta);
        }

        public void ToolStarted(string callId, string tool, bool approval)
        {
            lock (Tools)
                Tools.Add("started " + tool);
        }

        public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed)
        {
            lock (Tools)
                Tools.Add($"finished {tool} {outcome}");
        }

        public void Decided(CheckpointDecisions decisions)
        {
            lock (Checkpoints)
                Checkpoints.Add(decisions);
        }
    }
}
