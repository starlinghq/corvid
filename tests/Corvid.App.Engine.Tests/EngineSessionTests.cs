using Corvid.App.Core.Composition;
using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// The engine's session service end to end on fake models: a turn sent from the window streams the app's updates in the
/// contract's order, and the same session read back from the log folds to the transcript the live turn drew.
/// </summary>
public sealed class EngineSessionTests
{
    private const string Prompt = "what are my notes?";
    private const string Answer = "You have two notes: buy milk and call Ada.";

    private static FakeModels NotesTurn() => new(new FakeGenerator(Reply.Call("list_notes"), Reply.Say(Answer)));

    /// <summary>Runs one turn in a new session through a service, returning the session's id and what its subscriber heard.</summary>
    private static async Task<(string SessionId, IReadOnlyList<SessionUpdate> Live)> RunTurnAsync(EngineFixture fixture)
    {
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var recorder = new Recorder();
        using var subscription = await service.OpenAsync(id, recorder.Add, TestContext.Current.CancellationToken);
        // A new session has no transcript yet: only what it is beside one (its approval mode, the commands it offers, no changed
        // files, the generator its first turn starts on).
        Assert.All(subscription.History, update => Assert.True(update is ApprovalModeChanged or AvailableCommandsUpdate or ChangesUpdate { Session.IsEmpty: true }
            or EngineStatus { Model: "fake-gen" }, update.GetType().Name));

        await service.SendAsync(id, Prompt, TestContext.Current.CancellationToken);
        await service.IdleAsync();
        return (id, recorder.Updates);
    }

    /// <summary>The updates that both a live turn and a replay carry: usage comes from the feed alone (the log keeps decisions).</summary>
    private static List<SessionUpdate> Shared(IEnumerable<SessionUpdate> updates) => [.. updates.Where(update => update is not UsageUpdate)];

    [Fact]
    public async Task ATurnWithAToolCallStreamsTheContractsUpdatesInOrder()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (_, live) = await RunTurnAsync(fixture);
        var updates = Shared(live).Where(update => update is not CheckpointDecided).ToList();
        // A new session's first message titles it, as the rail shows it (until K-9 titles are events).
        Assert.Equal(Prompt, Assert.IsType<TitleChanged>(updates[0]).Title);
        updates.RemoveAt(0);

        // The prompt opens turn 1 on the generator; the call is announced running, then finished with its input and output.
        var kinds = string.Join(" ", updates.Select(update => update.GetType().Name).Where((name, i) =>
            i == 0 || name != "AgentMessageChunk" || updates[i - 1] is not AgentMessageChunk));
        // After the turn, the dock's change lists from the write ledger (empty: list_notes writes nothing). The response with
        // the call began step 1 of the turn's budget before the call started (turn progress).
        Assert.Equal("UserMessage TurnStarted TurnProgress ToolCall ToolCall ToolCallUpdate AgentMessageChunk TurnEnded ChangesUpdate", kinds);

        var prompt = Assert.IsType<UserMessage>(updates[0]);
        Assert.Equal(Prompt, prompt.Text);
        Assert.NotNull(prompt.Position);
        var started = Assert.IsType<TurnStarted>(updates[1]);
        Assert.Equal(1, started.Turn);
        Assert.Equal("fake-gen", started.Model);
        var step = Assert.IsType<TurnProgress>(updates[2]);
        Assert.Equal((1, started.MaxSteps), (step.Step, step.MaxSteps));
        Assert.NotNull(step.MaxSteps);

        var announced = Assert.IsType<ToolCall>(updates[3]);
        Assert.Equal("list_notes", announced.Tool);
        Assert.Equal(ToolCallStatus.InProgress, announced.Status);
        var described = Assert.IsType<ToolCall>(updates[4]);
        Assert.Equal(announced.CallId, described.CallId);
        Assert.Equal("{}", described.Input);
        var finished = Assert.IsType<ToolCallUpdate>(updates[5]);
        Assert.Equal(announced.CallId, finished.CallId);
        Assert.Equal(ToolCallStatus.Completed, finished.Status);
        Assert.Equal(ToolOutcome.Succeeded, finished.Outcome);
        Assert.Equal("buy milk; call Ada", finished.Output);
        Assert.NotNull(finished.Elapsed);

        // The answer streamed word by word, and nothing was sent twice.
        Assert.True(updates.OfType<AgentMessageChunk>().Count() > 1);
        Assert.Equal(Answer, string.Concat(updates.OfType<AgentMessageChunk>().Select(chunk => chunk.Text)));
        var ended = Assert.IsType<TurnEnded>(updates[^2]);
        Assert.Equal(1, ended.Turn);
        Assert.Equal(TurnOutcome.Answered, ended.Outcome);
        Assert.Null(ended.Error);

        // The checkpoints' decisions fall inside the turn, ingress's included, so the chips sit under its header.
        var all = live.ToList();
        var decisions = all.OfType<CheckpointDecided>().ToList();
        Assert.NotEmpty(decisions);
        Assert.All(decisions, decided => Assert.InRange(all.IndexOf(decided), all.FindIndex(u => u is TurnStarted) + 1, all.FindIndex(u => u is TurnEnded) - 1));
        Assert.Contains(decisions, decided => decided.Checkpoint == "ingress");
    }

    [Fact]
    public async Task ReplayingTheSessionFoldsToWhatTheLiveTurnShowed()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (id, live) = await RunTurnAsync(fixture);

        // A fresh service has no channel for the session: its history is the log's.
        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken);

        var shown = Describe(SessionFold.Replay(id, Shared(live)));
        var replayed = Describe(SessionFold.Replay(id, reopened.History));
        Assert.Equal(shown, replayed);
        Assert.Contains(replayed, line => line.StartsWith("ToolEntry list_notes Completed Succeeded {} buy milk; call Ada", StringComparison.Ordinal));
        Assert.Contains($"AnswerEntry {Answer}", replayed);
    }

    // A5 (A4 leftover): token use is in the log, so a reopened session's turn header has its tokens and the context gauge
    // is full before the next turn runs, as the live turn showed them.
    [Fact]
    public async Task AReplayedTurnShowsTheLiveTurnsTokensAndTheContextGauge()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (id, live) = await RunTurnAsync(fixture);
        var shown = SessionFold.Replay(id, live);

        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken);
        var replayed = SessionFold.Replay(id, reopened.History);

        var liveHeader = Assert.Single(shown.Transcript.OfType<TurnHeaderEntry>());
        var header = Assert.Single(replayed.Transcript.OfType<TurnHeaderEntry>());
        // Two generator calls of 42 in and 7 out each, as the fake provider reports them.
        Assert.Equal(98, liveHeader.Tokens);
        Assert.Equal(liveHeader.Tokens, header.Tokens);
        Assert.Equal(42, replayed.ContextTokens);
        Assert.Equal(shown.ContextWindow, replayed.ContextWindow);
        Assert.NotNull(replayed.ContextWindow);
    }

    [Fact]
    public async Task AReplayedTurnCarriesTheLiveTurnsGeneratorAndDecisions()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (id, live) = await RunTurnAsync(fixture);

        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken);

        // The generator the turn ran on, read from its first decision's TurnState, as the live turn named it.
        Assert.Equal("fake-gen", Assert.Single(reopened.History.OfType<TurnStarted>()).Model);
        // Every checkpoint's decisions, as the feed reported them live: the checkpoint, the decider and each answer.
        static List<string> Decisions(IEnumerable<SessionUpdate> updates) =>
        [
            .. updates.OfType<CheckpointDecided>().Select(decided => $"{decided.Checkpoint} {decided.Decider}: " +
                string.Join(", ", decided.Answers.Select(answer => $"{answer.Question} {answer.Action} {answer.Acted} {answer.Confidence:F3}"))),
        ];
        Assert.NotEmpty(Decisions(live));
        Assert.Equal(Decisions(live), Decisions(reopened.History));
        // The status bar names the generator the next turn starts on before one runs.
        Assert.Equal("fake-gen", SessionFold.Replay(id, reopened.History).Model);
    }

    [Fact]
    public async Task ACompactedSessionReplaysWithItsMarkerAsTheLiveOneShowedIt()
    {
        const string record = """{"goal":"ship it","constraints":["keep tests green"],"decisions":[],"files":["a.cs"],"open_questions":[],"next_step":"release"}""";
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(
            Reply.Say("First answer."), Reply.Say("Second answer."), Reply.Say(record), Reply.Say(record))));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var live = new Recorder();
        using var subscription = await service.OpenAsync(id, live.Add, TestContext.Current.CancellationToken);
        await service.SendAsync(id, "one", TestContext.Current.CancellationToken);
        await service.IdleAsync();
        await service.SendAsync(id, "two", TestContext.Current.CancellationToken);
        await service.IdleAsync();
        await service.RunCommandAsync(id, "/compact keep the plan", TestContext.Current.CancellationToken);
        await service.IdleAsync();
        var shown = SessionFold.Replay(id, [.. subscription.History, .. live.Updates]);
        var marker = Assert.Single(shown.Transcript.OfType<CompactionEntry>());

        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken);
        var replayed = SessionFold.Replay(id, reopened.History);

        var again = Assert.Single(replayed.Transcript.OfType<CompactionEntry>());
        Assert.Equal((marker.Compaction.FromTurn, marker.Compaction.ThroughTurn, marker.Compaction.Goal, marker.Compaction.Focus),
            (again.Compaction.FromTurn, again.Compaction.ThroughTurn, again.Compaction.Goal, again.Compaction.Focus));
        // Every checkpoint named as live, none blank.
        Assert.Equal(live.Updates.OfType<CheckpointDecided>().Select(decided => decided.Checkpoint),
            reopened.History.OfType<CheckpointDecided>().Select(decided => decided.Checkpoint));
        // And a view model opens it.
        using var view = new SessionViewModel(id, fresh, new Corvid.App.Core.Mvvm.InlineDispatcher(), fixture.Time);
        await view.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Contains(view.Blocks, block => block is CompactionBlock);
    }

    [Fact]
    public async Task ATurnTheLogHoldsNoAnswerForReplaysAsInterruptedAsTheLiveOneEnded()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        // An interrupted turn leaves its prompt and no answer in the log.
        fixture.Runtime.Log.Append(id, fixture.Runtime.Log.GetHead(id, TestContext.Current.CancellationToken),
            [Corvid.Abstractions.Events.EventData.Create(new Corvid.Abstractions.Events.UserMessage("stop halfway"))], TestContext.Current.CancellationToken);
        using var service = fixture.Sessions();

        using var opened = await service.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken);

        var header = Assert.Single(SessionFold.Replay(id, opened.History).Transcript.OfType<TurnHeaderEntry>());
        Assert.Equal((TurnOutcome.Cancelled, "interrupted"), (header.Outcome, header.Error));
    }

    [Fact]
    public async Task ASecondOpenOfALiveSessionGetsWhatTheFirstHeardAsItsHistory()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var first = new Recorder();
        using var a = await service.OpenAsync(id, first.Add, TestContext.Current.CancellationToken);
        await service.SendAsync(id, Prompt, TestContext.Current.CancellationToken);
        await service.IdleAsync();

        using var b = await service.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken);

        // The history keeps a streamed run of chunks as one chunk (so it grows by run, not by token), and folds the same.
        List<SessionUpdate> heard = [.. a.History, .. first.Updates];
        Assert.True(heard.OfType<AgentMessageChunk>().Count() > 1);
        Assert.Equal(Joined(heard), b.History);
        Assert.Single(b.History.OfType<AgentMessageChunk>());
        Assert.Equal(Describe(SessionFold.Replay(id, heard)), Describe(SessionFold.Replay(id, b.History)));
    }

    /// <summary>Updates with each run of consecutive answer (or thought) chunks joined into the first of the run.</summary>
    private static List<SessionUpdate> Joined(IEnumerable<SessionUpdate> updates)
    {
        var joined = new List<SessionUpdate>();
        foreach (var update in updates)
        {
            if (joined.Count > 0 && joined[^1] is AgentMessageChunk last && update is AgentMessageChunk next)
                joined[^1] = last with { Text = last.Text + next.Text };
            else if (joined.Count > 0 && joined[^1] is AgentThoughtChunk thought && update is AgentThoughtChunk more)
                joined[^1] = thought with { Text = thought.Text + more.Text };
            else
                joined.Add(update);
        }
        return joined;
    }

    [Fact]
    public async Task TheListShowsTheLogsConversationsWithTheirTitles()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (id, _) = await RunTurnAsync(fixture);
        using var service = fixture.Sessions();

        var sessions = await service.ListAsync(TestContext.Current.CancellationToken);

        var summary = Assert.Single(sessions, session => session.Id == id);
        Assert.Equal(Prompt, summary.Title);
        Assert.Equal("app", summary.Surface);
        Assert.Equal(1, summary.Turns);
        Assert.Equal(Answer, summary.Preview);
        Assert.Equal(Corvid.App.Core.Shell.SessionState.Idle, summary.State);
        Assert.Equal(fixture.Runtime.Builtin.Environment.Workspace, summary.Workspace);
    }

    [Fact]
    public async Task TheHermesImportsStagingSessionIsNotListedAsAConversation()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (id, _) = await RunTurnAsync(fixture);
        // What staging the import's facts writes to the log, in the session Memory stages them from.
        fixture.Runtime.Log.Append(Memory.EngineMemoryService.ImportSession, 0,
            [Corvid.Abstractions.Events.EventData.Create(new Corvid.Abstractions.Events.MemoryWritten("m-1", "stage"))], TestContext.Current.CancellationToken);
        using var service = fixture.Sessions();

        var sessions = await service.ListAsync(TestContext.Current.CancellationToken);

        Assert.Contains(sessions, session => session.Id == id);
        Assert.DoesNotContain(sessions, session => session.Id == Memory.EngineMemoryService.ImportSession);
    }

    [Fact]
    public async Task AnUnknownSessionCannotBeOpenedAndUnbuiltRequestsSayWhatBringsThem()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = fixture.Sessions();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.OpenAsync("cli-nowhere", _ => { }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.QueueAsync("cli-nowhere", "later", TestContext.Current.CancellationToken));
        // Steering (K-3) and answers (K-6) are built: an unknown session is unknown to them too, and the strip offers Ctrl+Enter.
        Assert.True(((ISessionService)service).CanSteer);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SteerAsync("cli-nowhere", "now", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.AnswerQuestionAsync("cli-nowhere", "q-1", "yes", TestContext.Current.CancellationToken));
        // A title never starts a session of its own.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RenameAsync("cli-nowhere", "a title", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("# Heading of my pasted notes\nthe first point")]
    [InlineData("# Heading typed in the composer\rthe first point")]
    [InlineData("![the diagram](docs/diagram.png) what does it show?")]
    public async Task PastedMarkdownIsNotAModeLine(string text)
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var head = fixture.Runtime.Log.GetHead(id, TestContext.Current.CancellationToken);

        await service.SendAsync(id, text, TestContext.Current.CancellationToken);
        await service.IdleAsync();

        Assert.True(fixture.Runtime.Log.GetHead(id, TestContext.Current.CancellationToken) > head);
    }

    [Fact]
    public async Task NothingReachesASubscriberAfterTheServiceIsDisposed()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        var service = fixture.Sessions();
        var recorder = new Recorder();
        using var subscription = await service.OpenAsync(id, recorder.Add, TestContext.Current.CancellationToken);
        await service.SendAsync(id, Prompt, TestContext.Current.CancellationToken);
        service.Dispose();
        var heard = recorder.Updates.Count;
        await service.IdleAsync();

        Assert.Equal(heard, recorder.Updates.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.OpenAsync(id, _ => { }, TestContext.Current.CancellationToken));
    }

    /// <summary>The transcript as lines of what the views draw, without times and ids that differ between a live turn and a replay.</summary>
    private static List<string> Describe(SessionView view) =>
    [
        .. view.Transcript.Select(entry => entry switch
        {
            UserEntry user => $"UserEntry {user.Turn} {user.Text} {user.Position}",
            TurnHeaderEntry header => $"TurnHeaderEntry {header.Number} {header.Running} {header.Outcome} {header.Error}",
            ToolEntry tool => $"ToolEntry {tool.Tool} {tool.Status} {tool.Outcome} {tool.Input} {tool.Output} {tool.Kind} {tool.Target} {string.Join(",", tool.Locations)} {tool.Diffs.Length}",
            AnswerEntry answer => $"AnswerEntry {answer.Text}",
            _ => entry.GetType().Name,
        }),
        $"turns {view.Turns} running {view.Running} plan {view.Plan.Length}",
    ];
}
