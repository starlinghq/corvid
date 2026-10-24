using System.Collections.Immutable;
using System.Text.Json;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;
using HostPlan = Corvid.Host.Sessions.PlanChanged;
using TodoItem = Corvid.Abstractions.Events.TodoItem;

namespace Corvid.App.Engine.Tests;

/// <summary>The host's stream (K-10) as the app's update contract, update by update.</summary>
public sealed class TranslatorTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "corvid-translator-work");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void APromptOpensANumberedTurnOnTheGeneratorAndTheNextPromptClosesAReplayedOne()
    {
        var translator = new SessionUpdateTranslator(Workspace);

        var first = translator.Translate(new PromptSent("hi", 3) { At = T0 });
        Assert.Collection(first,
            update => Assert.Equal(new UserMessage("hi") { Position = 3, At = T0 }, update),
            update => Assert.Equal(new TurnStarted(1, "") { At = T0 }, update));
        translator.Translate(new AnswerChunk("hello") { At = T0.AddSeconds(2) });

        var second = translator.Translate(new PromptSent("again", 9) { At = T0.AddSeconds(5) });

        var ended = Assert.IsType<TurnEnded>(second[0]);
        Assert.Equal((1, TurnOutcome.Answered, TimeSpan.FromSeconds(2), (string?)null), (ended.Turn, ended.Outcome, ended.Elapsed, ended.Error));
        Assert.Equal(T0.AddSeconds(2), ended.At);
        Assert.Equal(2, Assert.IsType<TurnStarted>(second[2]).Turn);
        Assert.True(translator.TurnOpen);
    }

    [Fact]
    public void AReplayedTurnWithoutAnAnswerSaysTheLogHasNoneRatherThanGuessing()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("hi", 1) { At = T0 });

        var ended = Assert.IsType<TurnEnded>(Assert.Single(translator.EndReplay()));
        Assert.Equal(TurnOutcome.Failed, ended.Outcome);
        Assert.Equal("The log holds no answer for this turn.", ended.Error);
        Assert.Empty(translator.EndReplay());
    }

    [Fact]
    public void AReplayOfATurnStillRunningLeavesItOpenAndTheLiveEndClosesIt()
    {
        var translator = new SessionUpdateTranslator(Workspace) { Model = "fake-gen" };
        translator.Translate(new PromptSent("hi", 1) { At = T0 });

        Assert.Empty(translator.EndReplay(running: true));
        var ended = Assert.IsType<TurnEnded>(Assert.Single(translator.EndTurn(TurnOutcome.Cancelled, T0.AddSeconds(1), TimeSpan.FromSeconds(1), "interrupted")));
        Assert.Equal((TurnOutcome.Cancelled, "interrupted"), (ended.Outcome, ended.Error));
        Assert.Empty(translator.EndTurn(TurnOutcome.Answered, T0));
    }

    [Fact]
    public void ALiveCallIsAnnouncedThenDescribedAndFinishedWithWorkspacePaths()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        var file = Path.Combine(Workspace, "src", "a.txt");

        var start = Assert.IsType<ToolCall>(Assert.Single(translator.Translate(
            new ToolCallStarted("c1", "write_file", ToolCallKinds.Edit, ToolCallState.InProgress) { Tool = "write_file" })));
        Assert.Equal(("c1", "write_file", ToolKind.Edit, ToolCallStatus.InProgress, (string?)null), (start.CallId, start.Tool, start.Kind, start.Status, start.Target));

        var done = translator.Translate(new ToolCallChanged("c1")
        {
            Tool = "write_file",
            Kind = ToolCallKinds.Edit,
            Status = ToolCallState.Completed,
            Outcome = "succeeded",
            Input = Json("""{"path":"src/a.txt","content":"new"}"""),
            OutputText = "wrote 3 bytes",
            Locations = [file],
            Diffs = [new FileDiff(file, "old", "new")],
            Elapsed = TimeSpan.FromMilliseconds(40),
        });

        var described = Assert.IsType<ToolCall>(done[0]);
        Assert.Equal(("src/a.txt", """{"path":"src/a.txt","content":"new"}"""), (described.Target, described.Input));
        Assert.Equal(["src/a.txt"], described.Locations);
        var update = Assert.IsType<ToolCallUpdate>(done[1]);
        Assert.Equal((ToolCallStatus.Completed, ToolOutcome.Succeeded, "wrote 3 bytes", TimeSpan.FromMilliseconds(40)),
            (update.Status, update.Outcome, update.Output, update.Elapsed));
        Assert.Equal(new FileEdit("src/a.txt", "old", "new"), Assert.Single(update.Diffs));
    }

    [Fact]
    public void AReplayedCallComesFinishedAndItsResultSaysSucceededOrFailed()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        var call = Assert.IsType<ToolCall>(Assert.Single(translator.Translate(
            new ToolCallStarted("c2", "shell: ls", ToolCallKinds.Execute, ToolCallState.Failed) { Tool = "shell", Input = Json("""{"command":"ls"}""") })));
        Assert.Equal((ToolCallStatus.Failed, "ls", """{"command":"ls"}"""), (call.Status, call.Target, call.Input));

        var result = Assert.IsType<ToolCallUpdate>(Assert.Single(translator.Translate(
            new ToolCallChanged("c2") { Tool = "shell", Status = ToolCallState.Failed, OutputText = "denied" })));
        Assert.Equal((ToolOutcome.Failed, "denied"), (result.Outcome, result.Output));
    }

    [Theory]
    [InlineData("memory_search", ToolCallKinds.Search, ToolKind.Memory)]
    [InlineData("notes__search", ToolCallKinds.Other, ToolKind.Mcp)]
    [InlineData("web_fetch", ToolCallKinds.Fetch, ToolKind.Fetch)]
    [InlineData("read_file", ToolCallKinds.Read, ToolKind.Read)]
    [InlineData("whatever", "unknown", ToolKind.Other)]
    public void AKindComesFromTheStreamWithTheDesignsMemoryAndMcpKindsByName(string tool, string kind, ToolKind expected) =>
        Assert.Equal(expected, SessionUpdateTranslator.Kind(kind, tool));

    [Fact]
    public void APathOutsideTheWorkspaceStaysAbsolute()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.txt");
        var call = Assert.IsType<ToolCall>(Assert.Single(translator.Translate(
            new ToolCallStarted("c3", "read_file", ToolCallKinds.Read, ToolCallState.Completed) { Tool = "read_file", Locations = [outside] })));
        Assert.Equal([outside], call.Locations);
    }

    [Fact]
    public void ThePlanAndTheDecisionsMapOntoTheirUpdatesAndCheckpointsAreLeftToRewind()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("plan it", 1));

        var plan = Assert.IsType<PlanUpdate>(Assert.Single(translator.Translate(
            new HostPlan([new TodoItem("1", "read the test", true), new TodoItem("2", "fix it", false)]))));
        Assert.Equal([new PlanEntry("read the test", PlanEntryStatus.Completed), new PlanEntry("fix it", PlanEntryStatus.Pending)], plan.Entries);

        var decided = Assert.IsType<CheckpointDecided>(Assert.Single(translator.Translate(new DecisionsMade(
            new CheckpointDecisions("ingress", "degraded", [new QuestionDecision("intent", 0.93, "task", true)])))));
        Assert.Equal(("ingress", "degraded"), (decided.Checkpoint, decided.Decider));
        Assert.Equal(new DecisionAnswer("intent", 0.93, "task", true), Assert.Single(decided.Answers));

        Assert.Empty(translator.Translate(new CheckpointLogged(new Corvid.Abstractions.Events.CheckpointTaken("k1", "rev"), 4)));
    }

    [Fact]
    public void DecisionsMadeBeforeThePromptIsLoggedBelongToTheTurnItOpens()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        var ingress = new DecisionsMade(new CheckpointDecisions("ingress", "degraded", [new QuestionDecision("intent", 0.5, "task", false)]));

        Assert.Empty(translator.Translate(ingress));
        var opened = translator.Translate(new PromptSent("hi", 1));

        Assert.Equal(["UserMessage", "TurnStarted", "CheckpointDecided"], opened.Select(update => update.GetType().Name));
        // Held decisions of a prompt that never arrived are dropped with the turn that did not open.
        translator.EndTurn(TurnOutcome.Answered, T0);
        Assert.Empty(translator.Translate(ingress));
        Assert.Empty(translator.EndTurn(TurnOutcome.Failed, T0));
        Assert.Equal(["UserMessage", "TurnStarted"], translator.Translate(new PromptSent("again", 2)).Select(update => update.GetType().Name));
    }

    [Fact]
    public void UntrustedTextPassesThroughForTheViewsToClean()
    {
        // The fold and the views clean what they draw (SafeText); the translator keeps the text as the engine gave it, so
        // an approval shown revealed later still has every character.
        var translator = new SessionUpdateTranslator(Workspace);
        var text = "look​ here";
        var chunk = Assert.IsType<AgentMessageChunk>(Assert.Single(translator.Translate(new AnswerChunk(text))));
        Assert.Equal(text, chunk.Text);
        Assert.Equal(ImmutableArray<string>.Empty, Assert.IsType<ToolCall>(Assert.Single(translator.Translate(
            new ToolCallStarted("c4", "x", ToolCallKinds.Other, ToolCallState.Pending)))).Locations);
    }
}
