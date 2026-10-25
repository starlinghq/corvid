using System.Collections.Immutable;
using System.Text.Json;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;
using HostPlan = Corvid.Host.Sessions.PlanChanged;
using TodoItem = Corvid.Abstractions.Events.TodoItem;
using Assessed = Corvid.Abstractions.Events.ApprovalAssessed;
using Resolutions = Corvid.Abstractions.Events.ApprovalResolutions;

namespace Corvid.App.Engine.Tests;

/// <summary>The host's stream (K-10) as the app's update contract, update by update.</summary>
public sealed class TranslatorTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "corvid-translator-work");
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void ASteerIsASteeredMessageInsideItsTurnAndAStepIsProgressOnlyWhileATurnIsOpen()
    {
        var translator = new SessionUpdateTranslator(Workspace) { MaxSteps = 8 };
        // No turn yet: a step has nothing to belong to.
        Assert.Empty(translator.Translate(new TurnStepped(1, 8) { At = T0 }));
        translator.Translate(new PromptSent("what's the weather?", 3) { At = T0 });

        Assert.Equal(new TurnProgress(1) { MaxSteps = 8, At = T0 }, Assert.Single(translator.Translate(new TurnStepped(1, 8) { At = T0 })));
        // A replayed step has no budget: the translator's, when it knows one.
        Assert.Equal(8, Assert.IsType<TurnProgress>(Assert.Single(translator.Translate(new TurnStepped(2, null) { At = T0 }))).MaxSteps);
        var steer = Assert.IsType<UserMessage>(Assert.Single(translator.Translate(new SteerSent("and tomorrow?", 9) { At = T0 })));
        Assert.Equal(new UserMessage("and tomorrow?") { Position = 9, Steered = true, At = T0 }, steer);
        // Still turn 1: a steer opens no turn.
        Assert.Equal(1, translator.Turn);
        Assert.True(translator.TurnOpen);
    }

    [Fact]
    public void ARunningCallsTailIsAnUpdateOfItsCard()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("run the tests", 1) { At = T0 });

        var update = Assert.IsType<ToolCallUpdate>(Assert.Single(translator.Translate(new ToolCallChanged("c1")
        {
            At = T0,
            Tool = "shell",
            Kind = ToolCallKinds.Execute,
            Status = ToolCallState.InProgress,
            Tail = "Passed 11   Failed 1",
        })));
        Assert.Equal(("c1", ToolCallStatus.InProgress, "Passed 11   Failed 1"), (update.CallId, update.Status, update.Tail));
        Assert.Null(update.Outcome);
    }

    [Fact]
    public void AQuestionStaysOpenPastItsTurnItsRepeatedAnswerIsNotShownTwiceAndTheNextMessageAnswersIt()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("tag the release", 1) { At = T0 });

        var asked = Assert.IsType<QuestionAsked>(Assert.Single(translator.Translate(
            new QuestionRaised("q-1", "Which version: 0.2.0 or 0.2.0-rc1?\nThe first tag decides the changelog.", ["0.2.0", "0.2.0-rc1"]) { At = T0 })));
        Assert.Equal(("q-1", "Which version: 0.2.0 or 0.2.0-rc1?", true), (asked.QuestionId, asked.Title, asked.AllowsFreeText));
        Assert.Equal(["0.2.0", "0.2.0-rc1"], asked.Options);
        // The delivered answer that repeats the question (ask_user's) is the block's already.
        Assert.Empty(translator.Translate(new AnswerChunk("Which version: 0.2.0 or 0.2.0-rc1?\nThe first tag decides the changelog.") { At = T0 }));
        translator.EndTurn(TurnOutcome.Clarifying, T0.AddSeconds(1));
        Assert.Equal("q-1", translator.OpenQuestion);

        var next = translator.Translate(new PromptSent("0.2.0-rc1", 7) { At = T0.AddSeconds(9) });

        Assert.Equal(new QuestionAnswered("q-1", "0.2.0-rc1") { At = T0.AddSeconds(9) }, next[0]);
        Assert.IsType<UserMessage>(next[1]);
        Assert.Null(translator.OpenQuestion);
        // The next turn's answer is shown whatever it says.
        Assert.Single(translator.Translate(new AnswerChunk("Which version: 0.2.0 or 0.2.0-rc1?\nThe first tag decides the changelog.") { At = T0 }));
    }

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
    public void AReplayedTurnWithoutAnAnswerEndsInterruptedAsTheLiveTurnDidNotFailed()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("hi", 1) { At = T0 });

        var ended = Assert.IsType<TurnEnded>(Assert.Single(translator.EndReplay()));
        Assert.Equal(TurnOutcome.Cancelled, ended.Outcome);
        Assert.Equal("interrupted", ended.Error);
        Assert.Empty(translator.EndReplay());
    }

    [Fact]
    public void AReplayedShellLineEndsAnsweredByItsCall()
    {
        // A4: a ! line's turn is the user's own shell call, with no answer by design.
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("!ls", 1) { At = T0 });
        translator.Translate(new ToolCallStarted("user-shell", "shell", ToolCallKinds.Execute, ToolCallState.Completed) { At = T0 });

        var ended = Assert.IsType<TurnEnded>(Assert.Single(translator.EndReplay()));
        Assert.Equal((TurnOutcome.Answered, (string?)null), (ended.Outcome, ended.Error));
    }

    [Fact]
    public void AReplayedTurnNamesItsGeneratorAndALiveOneTheSlots()
    {
        var translator = new SessionUpdateTranslator(Workspace) { Model = "live-gen" };

        var replayed = translator.Translate(new PromptSent("hi", 1) { At = T0, Generator = "qwen3.5-0.8b" });
        var live = translator.Translate(new PromptSent("again", 5) { At = T0 });

        Assert.Equal("qwen3.5-0.8b", Assert.IsType<TurnStarted>(replayed[1]).Model);
        Assert.Equal("live-gen", Assert.IsType<TurnStarted>(live[^1]).Model);
    }

    [Fact]
    public void ReplayedTaintAndCompactionBecomeTheRowAndTheMarker()
    {
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("one", 1) { At = T0 });
        translator.Translate(new PromptSent("two", 4) { At = T0 });

        var taint = Assert.IsType<TaintChanged>(Assert.Single(translator.Translate(new TaintNoted(true, "web_fetch https://example.org") { At = T0 })));
        var summary = new Corvid.Abstractions.Events.CompactionSummary("ship it", ["c"], ["d"], ["a.cs"], [], "next");
        var marker = Assert.IsType<CompactionUpdate>(Assert.Single(translator.Translate(
            new CompactionLogged(new Corvid.Abstractions.Events.Compacted(1, 4, summary, Summarizer: "qwen", Focus: "the plan"), 6) { Expanded = true, At = T0.AddMinutes(1) })));
        // A compaction between turns is not the replayed turn's end.
        Assert.Equal(TimeSpan.Zero, Assert.IsType<TurnEnded>(Assert.Single(translator.EndReplay())).Elapsed);

        Assert.Equal((true, "web_fetch https://example.org"), (taint.Tainted, taint.Source));
        Assert.True(translator.Tainted);
        Assert.Equal((1, 2, "ship it", "qwen", "the plan", true), (marker.FromTurn, marker.ThroughTurn, marker.Goal, marker.Summarizer, marker.Focus, marker.Expanded));
    }

    /// <summary>
    /// A4V2-4: a # line logs no message, only its memory gate's decision. Replayed after a turn's answer, that decision is
    /// neither the turn's nor its end, as live (where the line shows only as its notice); a turn's own memory_add asks
    /// before its answer, and its remember checkpoint's write after a remember that chose to write, and both stay the turn's.
    /// </summary>
    [Fact]
    public void AMemoryLinesGateAfterATurnIsNotTheTurnsAndDoesNotRunItsTimeOn()
    {
        static DecisionsMade Gate(DateTimeOffset at) =>
            new(new CheckpointDecisions("pre_execute", "degraded", [new QuestionDecision("memory_op", 0, "stage_for_review", false)])) { At = at };
        var translator = new SessionUpdateTranslator(Workspace);
        translator.Translate(new PromptSent("count", 1) { At = T0 });
        Assert.Single(translator.Translate(Gate(T0.AddSeconds(5))));
        translator.Translate(new AnswerChunk("Done.") { At = T0.AddSeconds(72) });
        translator.Translate(new DecisionsMade(new CheckpointDecisions("post_turn", "degraded",
            [new QuestionDecision("remember", 0, "write_nothing", false)])) { At = T0.AddSeconds(72) });

        // The # line, typed 19 s after the turn ended.
        Assert.Empty(translator.Translate(Gate(T0.AddSeconds(91))));
        // The next message closes the replayed turn where its answer was.
        var ended = Assert.IsType<TurnEnded>(translator.Translate(new PromptSent("!echo x", 9) { At = T0.AddSeconds(95) })[0]);
        Assert.Equal(TimeSpan.FromSeconds(72), ended.Elapsed);

        // A turn whose remember chose to write: the gate after its answer is its own.
        var writes = new SessionUpdateTranslator(Workspace);
        writes.Translate(new PromptSent("my deploy target is staging-2", 1) { At = T0 });
        writes.Translate(new AnswerChunk("Noted.") { At = T0.AddSeconds(2) });
        writes.Translate(new DecisionsMade(new CheckpointDecisions("post_turn", "degraded",
            [new QuestionDecision("remember", 0.9, "start_memory_write", true)])) { At = T0.AddSeconds(2) });
        Assert.Single(writes.Translate(Gate(T0.AddSeconds(3))));
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<TurnEnded>(Assert.Single(writes.EndReplay())).Elapsed);

        // Text first, then a memory_add call: its gate is the turn's.
        var adds = new SessionUpdateTranslator(Workspace);
        adds.Translate(new PromptSent("remember my editor", 1) { At = T0 });
        adds.Translate(new AnswerChunk("I'll note that.") { At = T0.AddSeconds(1) });
        adds.Translate(new ToolCallStarted("c1", "memory_add", ToolCallKinds.Other, ToolCallState.Completed) { Tool = "memory_add", At = T0.AddSeconds(2) });
        Assert.Single(adds.Translate(Gate(T0.AddSeconds(4))));

        // A ! line's turn is its call: a # line after it is no more its own than after an answer.
        var shell = new SessionUpdateTranslator(Workspace);
        shell.Translate(new PromptSent("!echo x", 1) { At = T0 });
        shell.Translate(new ToolCallStarted(Corvid.Kernel.Turns.TurnRunner.UserShellCall, "shell", ToolCallKinds.Execute, ToolCallState.Completed) { Tool = "shell", At = T0 });
        Assert.Empty(shell.Translate(Gate(T0.AddSeconds(30))));
        Assert.Equal(TimeSpan.Zero, Assert.IsType<TurnEnded>(Assert.Single(shell.EndReplay())).Elapsed);

        // Before any turn, a # line's gate waits for no prompt.
        var first = new SessionUpdateTranslator(Workspace);
        Assert.Empty(first.Translate(Gate(T0)));
        Assert.Equal(["UserMessage", "TurnStarted"], first.Translate(new PromptSent("hi", 3) { At = T0 }).Select(update => update.GetType().Name));
    }

    /// <summary>A4V2-5: a ! line's turn runs no generator, so its header names none, live as on replay.</summary>
    [Fact]
    public void AShellLinesTurnNamesNoGenerator()
    {
        var translator = new SessionUpdateTranslator(Workspace) { Model = "fake-gen", ShellLine = true };

        var shell = Assert.IsType<TurnStarted>(translator.Translate(new PromptSent("!echo x", 1) { At = T0 })[1]);
        var next = Assert.Single(translator.Translate(new PromptSent("hello", 4) { At = T0 }).OfType<TurnStarted>());

        Assert.Equal(("", "fake-gen"), (shell.Model, next.Model));
        var header = Assert.Single(SessionFold.Replay("s", [shell]).Transcript.OfType<TurnHeaderEntry>());
        Assert.Empty(header.Models);
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
        var text = "look\u200B here";
        var chunk = Assert.IsType<AgentMessageChunk>(Assert.Single(translator.Translate(new AnswerChunk(text))));
        Assert.Equal(text, chunk.Text);
        Assert.Equal(ImmutableArray<string>.Empty, Assert.IsType<ToolCall>(Assert.Single(translator.Translate(
            new ToolCallStarted("c4", "x", ToolCallKinds.Other, ToolCallState.Pending)))).Locations);
    }

    private static ToolCallChanged Finished(Assessed? assessed = null, bool? readOnly = null, string? checkpoint = null, CallApproval? approval = null) =>
        new("c1") { Status = ToolCallState.Completed, Assessment = assessed, ReadOnly = readOnly, CheckpointBefore = checkpoint, Approval = approval };

    [Fact]
    public void ACallSmartModeRanUnaskedSaysTheModeTheRiskAndWhatItDoes()
    {
        var assessed = new Assessed("c1", Resolutions.Automatic, "smart", [new("risk", "approval_mode: level 1")],
            Risk: new("d-1", "approval_mode", 1, "low", 0.93, true, "kev-0.8b"), Effects: ["write"], Contained: true);

        var update = Assert.IsType<ToolCallUpdate>(Assert.Single(new SessionUpdateTranslator(Workspace).Translate(Finished(assessed, false, "c-44"))));

        var verdict = update.Verdict!;
        Assert.Equal((VerdictKind.Mode, (Corvid.App.Core.Shell.ApprovalMode?)Corvid.App.Core.Shell.ApprovalMode.Smart, (int?)1, (double?)0.93, "c-44", "in workspace, no network"),
            (verdict.Kind, verdict.Mode, verdict.RiskLevel, verdict.RiskConfidence, verdict.CheckpointId, verdict.Detail));
        // An uncalibrated answer says so.
        var degraded = SessionUpdateTranslator.Verdict(Finished(assessed with { Risk = assessed.Risk! with { Calibrated = false, Decider = "degraded" } }))!;
        Assert.Equal("in workspace, no network, the risk answer was not calibrated (degraded)", degraded.Detail);
    }

    [Fact]
    public void RulesHardlineAndAMissingApproverAreNamedAsTheyDecided()
    {
        static Assessed Of(string resolution, string? rule = null, string? scope = null, params string[] reasons) =>
            new("c1", resolution, "manual", [.. reasons.Select(reason => new Corvid.Abstractions.Events.AssessmentReason("code", reason))], rule, scope);
        static CallVerdict Verdict(Assessed assessed) => SessionUpdateTranslator.Verdict(Finished(assessed, false, "c-9"))!;

        var session = Verdict(Of(Resolutions.RuleAllowed, "`dotnet test` under /w", "session"));
        Assert.Equal((VerdictKind.SessionRule, "`dotnet test` under /w", "c-9"), (session.Kind, session.Rule, session.CheckpointId));
        Assert.Equal(VerdictKind.AlwaysRule, Verdict(Of(Resolutions.RuleAllowed, "`rm`", "always")).Kind);
        var denied = Verdict(Of(Resolutions.RuleDenied, "`curl` for example.org", "always", "a remembered denial matches"));
        Assert.Equal((VerdictKind.RuleDenied, "`curl` for example.org", (string?)null), (denied.Kind, denied.Detail, denied.CheckpointId));
        var hardline = Verdict(Of(Resolutions.Hardline, null, null, "recursive delete outside the workspace"));
        Assert.Equal((VerdictKind.Hardline, "recursive delete outside the workspace"), (hardline.Kind, hardline.Detail));
        var nobody = Verdict(Of(Resolutions.NoApprover, null, null, "manual mode asks before every side effect"));
        Assert.Equal(VerdictKind.NoApprover, nobody.Kind);
        Assert.Equal(VerdictKind.ReadOnly, Verdict(new("c1", Resolutions.Automatic, "smart", [new("read_only", "reads only")])).Kind);
    }

    [Fact]
    public void AReadOnlyToolAnAnsweredPromptAndNothingKnownEachKeepTheirVerdict()
    {
        Assert.Equal(VerdictKind.ReadOnly, SessionUpdateTranslator.Verdict(Finished(readOnly: true))!.Kind);
        // Nothing known leaves the card's verdict (the live prompter's) as it was.
        Assert.Null(SessionUpdateTranslator.Verdict(Finished(readOnly: false)));
        Assert.Null(SessionUpdateTranslator.Verdict(Finished()));
        var approved = SessionUpdateTranslator.Verdict(Finished(readOnly: true, checkpoint: "c-3",
            approval: new CallApproval("a-1", true, "user: approved (session)") { Scope = "session" }))!;
        Assert.Equal((VerdictKind.ApprovedSession, "c-3"), (approved.Kind, approved.CheckpointId));
        var refused = SessionUpdateTranslator.Verdict(Finished(checkpoint: "c-3", approval: new CallApproval("a-1", false, "user: denied (once)")))!;
        Assert.Equal((VerdictKind.Denied, (string?)null), (refused.Kind, refused.CheckpointId));
    }
}
