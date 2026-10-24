using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class SessionFoldTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 14, 4, 12, TimeSpan.Zero);

    private static T At<T>(T update, double seconds)
        where T : SessionUpdate => update with { At = Start.AddSeconds(seconds) };

    /// <summary>A turn as the engine reports it: prompt, ingress, an escalation, a read, a thought, a running shell call, text.</summary>
    private static List<SessionUpdate> RecordedTurn() =>
    [
        At(new UserMessage("why does the sandbox test fail on CI?") { Position = 40 }, -3),
        At(new TurnStarted(12, "qwen3.5-0.8b") { MaxSteps = 6 }, 0),
        At(new CheckpointDecided("ingress", "Kev-0.8B",
        [
            new DecisionAnswer("intent", 0.94, "task", true),
            new DecisionAnswer("clarify", 0.41, "no", false),
        ]), 0),
        At(new ModelSwitched("generator", "qwen3.5-0.8b", "claude", ModelSwitchKind.Escalated, "step budget") { Redacted = 2, Cloud = true }, 1),
        At(new TurnProgress(1), 1),
        At(new ToolCall("call-1", "read_file", ToolKind.Read, ToolCallStatus.InProgress) { Target = "ci.yml", Verdict = new CallVerdict(VerdictKind.ReadOnly) }, 2),
        At(new ToolCallUpdate("call-1") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Elapsed = TimeSpan.FromSeconds(0.3) }, 2.3),
        At(new AgentThoughtChunk("The control step "), 28),
        At(new AgentThoughtChunk("runs on the host."), 30),
        At(new TurnProgress(4), 32),
        At(new ToolCall("call-4", "shell", ToolKind.Execute, ToolCallStatus.InProgress) { Target = "dotnet test" }, 36),
        At(new AgentMessageChunk("The failure is "), 40),
        At(new AgentMessageChunk("the control step."), 44),
        At(new ToolCallUpdate("call-4") { Tail = "Passed 11   Failed 1" }, 47),
    ];

    [Fact]
    public void ATurnFoldsIntoEntriesInReadingOrderWithTheThoughtAtItsFoot()
    {
        var state = SessionFold.Replay("cli-9f2e", RecordedTurn());

        Assert.Equal(
            ["UserEntry", "TurnHeaderEntry", "DecisionsEntry", "DecisionsEntry", "SwitchEntry", "ToolEntry", "ToolEntry", "AnswerEntry", "ThoughtEntry"],
            state.Transcript.Select(entry => entry.GetType().Name));
        Assert.All(state.Transcript, entry => Assert.Equal(12, entry.Turn));

        var header = Assert.IsType<TurnHeaderEntry>(state.Transcript[1]);
        Assert.Equal(["qwen3.5-0.8b", "claude"], header.Models);
        Assert.True(header.Running);
        Assert.Equal(4, header.Step);
        Assert.True(Assert.IsType<DecisionsEntry>(state.Transcript[2]).Summary);
        Assert.False(Assert.IsType<DecisionsEntry>(state.Transcript[3]).Summary);

        var read = Assert.IsType<ToolEntry>(state.Transcript[5]);
        Assert.Equal((ToolCallStatus.Completed, ToolOutcome.Succeeded), (read.Status, read.Outcome));
        var shell = Assert.IsType<ToolEntry>(state.Transcript[6]);
        Assert.Equal(ToolCallStatus.InProgress, shell.Status);
        Assert.Equal("Passed 11   Failed 1", shell.Tail);
        Assert.Equal(Start.AddSeconds(36), shell.StartedAt);

        var answer = Assert.IsType<AnswerEntry>(state.Transcript[7]);
        Assert.Equal("The failure is the control step.", answer.Text);
        Assert.True(answer.Streaming);
        // Thinking ran from the first thought chunk until the next update: four seconds.
        var thought = Assert.IsType<ThoughtEntry>(state.Transcript[8]);
        Assert.Equal(TimeSpan.FromSeconds(4), thought.Thinking);
        Assert.False(thought.Streaming);

        Assert.True(state.Running);
        Assert.Equal(12, state.Turn);
        Assert.Equal("claude", state.Model);
        Assert.Equal(Start.AddSeconds(36), state.ActivitySince);
        Assert.Equal(Start.AddSeconds(47), state.LastActivity);
        Assert.Single(state.Decisions);
    }

    [Fact]
    public void FoldingOneUpdateAtATimeIsReplaying()
    {
        var updates = RecordedTurn();
        var state = SessionFold.Start("cli-9f2e");
        foreach (var update in updates)
            state = SessionFold.Apply(state, update);

        var replayed = SessionFold.Replay("cli-9f2e", updates);
        Assert.Equal(replayed.Transcript.Select(entry => $"{entry.GetType().Name}:{entry.Id}"), state.Transcript.Select(entry => $"{entry.GetType().Name}:{entry.Id}"));
        Assert.Equal(replayed.LastActivity, state.LastActivity);
    }

    [Fact]
    public void AnInterruptedTurnStopsStreamingCancelsWhatRunsAndKeepsTheWorkSoFar()
    {
        var state = SessionFold.Replay("cli-9f2e", [.. RecordedTurn(), At(new TurnEnded(12, TurnOutcome.Cancelled), 50)]);

        Assert.False(state.Running);
        var header = Assert.IsType<TurnHeaderEntry>(state.Transcript[1]);
        Assert.False(header.Running);
        Assert.Equal(TurnOutcome.Cancelled, header.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(50), header.Elapsed);
        var shell = state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-4");
        Assert.Equal((ToolCallStatus.Failed, ToolOutcome.Cancelled), (shell.Status, shell.Outcome));
        var answer = state.Transcript.OfType<AnswerEntry>().Single();
        Assert.False(answer.Streaming);
        Assert.Equal("The failure is the control step.", answer.Text);
    }

    [Fact]
    public void TheApprovalCardFollowsItsToolCardAndTheSummaryShowsTheCheckpointThatAsked()
    {
        var deadline = Start.AddMinutes(5);
        List<SessionUpdate> updates =
        [
            .. RecordedTurn(),
            At(new ToolCallUpdate("call-4") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded }, 48),
            At(new ToolCall("call-5", "shell", ToolKind.Execute, ToolCallStatus.Pending) { Target = "rm -rf build/" }, 49),
            At(new CheckpointDecided("pre_execute", "Kev-0.8B", [new DecisionAnswer("risk", 0.86, "ask", true) { Option = "3" }]), 49),
            At(new ApprovalRequested("a-32", "shell", "rm -rf build/", deadline) { CallId = "call-5" }, 49),
            At(new ApprovalRequested("a-33", "patch_file", "patch_file a.cs", deadline) { CallId = "call-6" }, 49),
        ];
        var state = SessionFold.Replay("cli-9f2e", updates);

        Assert.True(state.WaitingOnUser);
        Assert.Equal(["a-32", "a-33"], state.Approvals.Select(request => request.ApprovalId));
        var toolIndex = state.Transcript.FindIndex(entry => entry.Id == SessionFold.ToolId("call-5"));
        var card = Assert.IsType<ApprovalEntry>(state.Transcript[toolIndex + 1]);
        Assert.Equal(("a-32", 1, 2, "a-33"), (card.Request.ApprovalId, card.Position, card.Count, card.Next?.ApprovalId));
        Assert.Equal("a-32", Assert.IsType<ToolEntry>(state.Transcript[toolIndex]).ApprovalId);
        var summary = state.Transcript.OfType<DecisionsEntry>().Single(entry => entry.Summary);
        Assert.Equal("pre_execute", summary.Group.Checkpoint);

        // Answered for the session: the card goes, the tool card says why it runs, and a-33 is next (its call not yet announced).
        state = SessionFold.Apply(state, At(new ApprovalResolved("a-32", ApprovalOutcome.Approved)
        {
            Scope = ApprovalScope.Session,
            Rule = "`rm -r` under build/",
        }, 60));
        var tool = state.Transcript.OfType<ToolEntry>().Single(entry => entry.CallId == "call-5");
        Assert.Null(tool.ApprovalId);
        Assert.Equal((VerdictKind.ApprovedSession, "`rm -r` under build/"), (tool.Verdict!.Kind, tool.Verdict.Rule));
        var next = state.Transcript.OfType<ApprovalEntry>().Single();
        Assert.Equal(("a-33", 1, 1), (next.Request.ApprovalId, next.Position, next.Count));
        Assert.IsType<ThoughtEntry>(state.Transcript[^1]);

        // Its call announced: the card moves under it.
        state = SessionFold.Apply(state, At(new ToolCall("call-6", "patch_file", ToolKind.Edit, ToolCallStatus.Pending), 61));
        var patch = state.Transcript.FindIndex(entry => entry.Id == SessionFold.ToolId("call-6"));
        Assert.IsType<ApprovalEntry>(state.Transcript[patch + 1]);
        Assert.Equal("a-33", Assert.IsType<ToolEntry>(state.Transcript[patch]).ApprovalId);

        // Denied at the deadline: the call never runs, the card closes, and the summary goes back to ingress.
        state = SessionFold.Apply(state, At(new ApprovalResolved("a-33", ApprovalOutcome.TimedOut), 300));
        var denied = state.Transcript.OfType<ToolEntry>().Single(entry => entry.CallId == "call-6");
        Assert.Equal((ToolCallStatus.Failed, ToolOutcome.Denied, VerdictKind.TimedOut), (denied.Status, denied.Outcome, denied.Verdict!.Kind));
        Assert.DoesNotContain(state.Transcript, entry => entry is ApprovalEntry);
        Assert.Equal("ingress", state.Transcript.OfType<DecisionsEntry>().Single(entry => entry.Summary).Group.Checkpoint);
    }

    [Fact]
    public void AStaleApprovalIsClosedWhenItsTurnEnds()
    {
        var state = SessionFold.Replay("cli-9f2e",
        [
            .. RecordedTurn(),
            At(new ToolCall("call-5", "shell", ToolKind.Execute, ToolCallStatus.Pending), 49),
            At(new ApprovalRequested("a-32", "shell", "rm -rf build/", Start.AddMinutes(5)) { CallId = "call-5" }, 49),
            At(new TurnEnded(12, TurnOutcome.Cancelled), 52),
        ]);

        Assert.Empty(state.Approvals);
        Assert.DoesNotContain(state.Transcript, entry => entry is ApprovalEntry);
        var tool = state.Transcript.OfType<ToolEntry>().Single(entry => entry.CallId == "call-5");
        Assert.Equal((VerdictKind.Cancelled, ToolOutcome.Cancelled), (tool.Verdict!.Kind, tool.Outcome));
    }

    [Fact]
    public void ASteerLandsInTheRunningTurnAboveItsThought()
    {
        var state = SessionFold.Replay("cli-9f2e", [.. RecordedTurn(), At(new UserMessage("also check the retry count") { Steered = true }, 48)]);

        var steer = state.Transcript.OfType<UserEntry>().Single(entry => entry.Steered);
        Assert.Equal(12, steer.Turn);
        Assert.Equal(state.Transcript.Count - 2, state.Transcript.IndexOf(steer));
        // Text after it starts a new answer block.
        state = SessionFold.Apply(state, At(new AgentMessageChunk("Noted."), 49));
        Assert.Equal("Noted.", Assert.IsType<AnswerEntry>(state.Transcript[^2]).Text);
        Assert.False(state.Transcript.OfType<AnswerEntry>().First().Streaming);
    }

    [Fact]
    public void TheNextPromptStartsTheNextTurn()
    {
        var state = SessionFold.Replay("cli-9f2e",
        [
            .. RecordedTurn(),
            At(new TurnEnded(12, TurnOutcome.Answered) { Tokens = 1300, Cost = 0.05m }, 51),
            At(new UserMessage("also check the retry count"), 52),
            At(new TurnStarted(13, "claude"), 52),
        ]);

        var prompt = state.Transcript.OfType<UserEntry>().Last();
        Assert.Equal(13, prompt.Turn);
        Assert.Equal(13, Assert.IsType<TurnHeaderEntry>(state.Transcript[^1]).Number);
        var previous = state.Transcript.OfType<TurnHeaderEntry>().First();
        Assert.Equal((1300, 0.05m, false), (previous.Tokens, previous.Cost, previous.Running));
        Assert.Equal(2, state.Turns);
    }

    [Fact]
    public void TaintStartsAndClearsWithARowEachTime()
    {
        var state = SessionFold.Replay("s",
        [
            At(new TaintChanged(true) { Source = "web_fetch docs.example.org" }, 1),
            At(new TaintChanged(true) { Source = "web_fetch docs.example.org" }, 2),
            At(new TaintChanged(false), 3),
        ]);

        Assert.Equal([true, false], state.Transcript.OfType<TaintEntry>().Select(entry => entry.Tainted));
        Assert.False(state.Tainted);
        Assert.Null(state.TaintSource);
    }

    [Fact]
    public void ARewindTakesTheTurnsOutOfTheTranscriptAndLeavesAMarker()
    {
        var state = SessionFold.Replay("cli-9f2e",
        [
            .. RecordedTurn(),
            At(new TurnEnded(12, TurnOutcome.Answered), 51),
            At(new CommandResult("/status") { Lines = ["ok"] }, 52),
            At(new SessionRewound(12, RewindScope.FilesAndConversation, "rollback") { Restored = ["a.cs", "b.cs"], Merged = ["c.cs"] }, 53),
        ]);

        Assert.Equal(["CommandEntry", "RewindEntry"], state.Transcript.Select(entry => entry.GetType().Name));
        var marker = Assert.IsType<RewindEntry>(state.Transcript[1]);
        Assert.Equal(2, marker.Rewind.Restored.Length);

        var filesOnly = SessionFold.Replay("s", [.. RecordedTurn(), At(new SessionRewound(12, RewindScope.FilesOnly, "undo"), 53)]);
        Assert.Contains(filesOnly.Transcript, entry => entry is TurnHeaderEntry);
    }

    /// <summary>Two turns of calls in the ledger and the change lists the engine reported for them.</summary>
    private static List<SessionUpdate> TwoTurnsOfChanges() =>
    [
        At(new TurnStarted(10, "qwen3.5-0.8b"), 0),
        At(new CheckpointRecorded("c-1", CheckpointKind.Before) { CallId = "call-a", Tool = "write_file", Turn = 10 }, 1),
        At(new CheckpointRecorded("c-2", CheckpointKind.After) { CallId = "call-a", Turn = 10, Changes = [new FileChange("a.cs", 3, 0)] }, 2),
        At(new TurnEnded(10, TurnOutcome.Answered), 3),
        At(new TurnStarted(11, "qwen3.5-0.8b"), 4),
        At(new CheckpointRecorded("c-3", CheckpointKind.Before) { CallId = "call-b", Tool = "patch_file", Turn = 11 }, 5),
        At(new CheckpointRecorded("c-4", CheckpointKind.After) { CallId = "call-b", Turn = 11, Changes = [new FileChange("a.cs", 2, 1), new FileChange("b.cs", 5, 5)] }, 6),
        At(new ChangesUpdate
        {
            Turn = [new FileChange("a.cs", 2, 1), new FileChange("b.cs", 5, 5)],
            Session = [new FileChange("a.cs", 5, 1), new FileChange("b.cs", 5, 5), new FileChange("vault.json", 0, 0) { Readable = false }],
        }, 7),
        At(new TurnEnded(11, TurnOutcome.Answered), 8),
    ];

    [Fact]
    public void ARestoreTakesItsTurnsAndFilesOutOfTheChangeListsAndMarksTheLedger()
    {
        var state = SessionFold.Replay("s",
        [
            .. TwoTurnsOfChanges(),
            At(new CheckpointRecorded("c-5", CheckpointKind.Restore) { Target = "rollback to before c-3", Turn = 11 }, 9),
            At(new SessionRewound(11, RewindScope.FilesAndConversation, "rollback") { Restored = ["a.cs", "b.cs"], CheckpointId = "c-5" }, 10),
        ]);

        // Turn 11 left: the latest turn is 10 again, and its changes are what the ledger has of it.
        Assert.Equal(10, state.Turn);
        Assert.False(state.Running);
        Assert.Equal([new FileChange("a.cs", 3, 0)], state.TurnChanges);
        // a.cs is counted again from turn 10's call alone; b.cs went back entirely; the unreadable file is kept.
        Assert.Equal([new FileChange("a.cs", 3, 0), new FileChange("vault.json", 0, 0) { Readable = false }], state.SessionChanges);
        // Every row stays in the ledger; turn 11's changes are marked undone, turn 10's are not.
        Assert.Equal(3, state.Ledger.Count);
        Assert.Empty(state.Ledger[0].UndonePaths);
        Assert.Equal(["a.cs", "b.cs"], state.Ledger[1].UndonePaths);
        Assert.Empty(state.Ledger[1].LiveChanges);

        // The next prompt is turn 11 again.
        state = SessionFold.Apply(state, At(new UserMessage("again"), 11));
        Assert.Equal(11, state.Transcript.OfType<UserEntry>().Last().Turn);
    }

    [Fact]
    public void AFilesOnlyRestoreKeepsTheTurnAndARefusedOneChangesNothing()
    {
        var filesOnly = SessionFold.Replay("s",
        [
            .. TwoTurnsOfChanges(),
            At(new SessionRewound(11, RewindScope.FilesOnly, "revert") { Merged = ["b.cs"], CheckpointId = "c-5" }, 9),
        ]);

        Assert.Equal(11, filesOnly.Turn);
        Assert.Contains(filesOnly.Transcript, entry => entry is TurnHeaderEntry { Number: 11 });
        Assert.Equal(["a.cs"], filesOnly.TurnChanges.Select(change => change.Path));
        Assert.Equal(["a.cs", "vault.json"], filesOnly.SessionChanges.Select(change => change.Path));
        Assert.Equal(["b.cs"], filesOnly.Ledger[1].UndonePaths);
        Assert.Equal([new FileChange("a.cs", 2, 1)], filesOnly.Ledger[1].LiveChanges);

        var conversationOnly = SessionFold.Replay("s", [.. TwoTurnsOfChanges(), At(new SessionRewound(11, RewindScope.ConversationOnly, "rollback"), 9)]);
        Assert.Equal(10, conversationOnly.Turn);
        Assert.Equal(3, conversationOnly.SessionChanges.Length);
        Assert.All(conversationOnly.Ledger, row => Assert.Empty(row.UndonePaths));

        var before = SessionFold.Replay("s", TwoTurnsOfChanges());
        var refused = SessionFold.Apply(before, At(new SessionRewound(11, RewindScope.FilesAndConversation, "rollback") { Refused = true, Restored = ["a.cs"] }, 9));
        Assert.Equal((before.Turn, before.Ledger, before.SessionChanges, before.TurnChanges), (refused.Turn, refused.Ledger, refused.SessionChanges, refused.TurnChanges));
    }

    [Fact]
    public void PanelsAndTheStatusBarReadTheirStateFromTheStream()
    {
        var state = SessionFold.Replay("cli-9f2e",
        [
            At(new TitleChanged("Fix the flaky sandbox test on CI") { Generated = true }, 0),
            At(new SessionForked("cli-8c11", 9), 0),
            At(new GoalChanged("  keep CI green on main "), 0),
            At(new ApprovalModeChanged(ApprovalMode.Smart), 0),
            At(new EngineStatus { Generator = new GeneratorStatus("claude", "cuda:0") { Escalated = true }, Decider = "Kev-0.8B", Branch = "main" }, 0),
            At(new UsageUpdate { ContextTokens = 76_000, ContextWindow = 200_000, CompactAt = 0.7, Breakdown = [new ContextPart("system", 2100)] }, 0),
            At(new PlanUpdate([new PlanEntry("a", PlanEntryStatus.Completed), new PlanEntry("b", PlanEntryStatus.Pending)]), 0),
            At(new ChangesUpdate { Turn = [new FileChange("a.cs", 1, 2)], Session = [new FileChange("a.cs", 3, 4), new FileChange("vault.json", 0, 0) { Readable = false }] }, 0),
            At(new QueueChanged([new QueuedMessage("q-1", "later")]), 0),
            At(new QuestionAsked("q-7", "0.2.0 or 0.2.0-rc1?") { Options = ["0.2.0", "0.2.0-rc1"] }, 0),
            At(new CheckpointRecorded("c-44", CheckpointKind.Before) { CallId = "call-4", Tool = "shell", Turn = 12 }, 1),
            At(new CheckpointRecorded("c-45", CheckpointKind.After) { CallId = "call-4", Changes = [new FileChange("a.cs", 1, 0)] }, 2),
            At(new DriverChanged(true) { Holder = "a terminal" }, 3),
        ]);

        Assert.Equal(("Fix the flaky sandbox test on CI", "cli-8c11", 9, "keep CI green on main"), (state.Title, state.ForkOf, state.ForkTurn, state.Goal));
        Assert.Equal(ApprovalMode.Smart, state.Mode);
        Assert.Equal(0.38, state.ContextFraction!.Value, 3);
        Assert.Equal("claude", state.Model);
        Assert.Equal(2, state.Plan.Length);
        Assert.Equal(2, state.SessionChanges.Length);
        Assert.Single(state.Queue);
        Assert.Equal("q-7", state.Question?.QuestionId);
        Assert.True(state.WaitingOnUser);
        var row = Assert.Single(state.Ledger);
        Assert.Equal(("c-44", "c-45", 12, 1), (row.BeforeId, row.AfterId, row.Turn, row.Changes.Length));
        Assert.True(state.ReadOnly);

        state = SessionFold.Apply(state, At(new QuestionAnswered("q-7", "0.2.0"), 4));
        Assert.Null(state.Question);
        Assert.Equal("0.2.0", Assert.IsType<QuestionEntry>(state.Transcript.Single(entry => entry is QuestionEntry)).Answer);
    }

    [Fact]
    public void AnUnknownUpdateChangesNothingButTheClock()
    {
        var before = SessionFold.Replay("s", RecordedTurn());

        var after = SessionFold.Apply(before, new FutureUpdate { At = Start.AddMinutes(1) });

        Assert.Equal(before.Transcript, after.Transcript);
        Assert.Equal(Start.AddMinutes(1), after.LastActivity);
    }

    private sealed record FutureUpdate : SessionUpdate;
}
