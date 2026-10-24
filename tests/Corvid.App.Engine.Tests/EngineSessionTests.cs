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
        // A new session has no transcript yet: only what it is beside one (its approval mode, the commands it offers, no changed files).
        Assert.All(subscription.History, update => Assert.True(update is ApprovalModeChanged or AvailableCommandsUpdate or ChangesUpdate { Session.IsEmpty: true },
            update.GetType().Name));

        await service.SendAsync(id, Prompt, TestContext.Current.CancellationToken);
        await service.IdleAsync();
        return (id, recorder.Updates);
    }

    /// <summary>The updates that both a live turn and a replay carry: decisions and usage come from the feed alone.</summary>
    private static List<SessionUpdate> Shared(IEnumerable<SessionUpdate> updates) => [.. updates.Where(update => update is not (CheckpointDecided or UsageUpdate))];

    [Fact]
    public async Task ATurnWithAToolCallStreamsTheContractsUpdatesInOrder()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var (_, live) = await RunTurnAsync(fixture);
        var updates = Shared(live);
        // A new session's first message titles it, as the rail shows it (until K-9 titles are events).
        Assert.Equal(Prompt, Assert.IsType<TitleChanged>(updates[0]).Title);
        updates.RemoveAt(0);

        // The prompt opens turn 1 on the generator; the call is announced running, then finished with its input and output.
        var kinds = string.Join(" ", updates.Select(update => update.GetType().Name).Where((name, i) =>
            i == 0 || name != "AgentMessageChunk" || updates[i - 1] is not AgentMessageChunk));
        // After the turn, the dock's change lists from the write ledger (empty: list_notes writes nothing).
        Assert.Equal("UserMessage TurnStarted ToolCall ToolCall ToolCallUpdate AgentMessageChunk TurnEnded ChangesUpdate", kinds);

        var prompt = Assert.IsType<UserMessage>(updates[0]);
        Assert.Equal(Prompt, prompt.Text);
        Assert.NotNull(prompt.Position);
        var started = Assert.IsType<TurnStarted>(updates[1]);
        Assert.Equal(1, started.Turn);
        Assert.Equal("fake-gen", started.Model);

        var announced = Assert.IsType<ToolCall>(updates[2]);
        Assert.Equal("list_notes", announced.Tool);
        Assert.Equal(ToolCallStatus.InProgress, announced.Status);
        var described = Assert.IsType<ToolCall>(updates[3]);
        Assert.Equal(announced.CallId, described.CallId);
        Assert.Equal("{}", described.Input);
        var finished = Assert.IsType<ToolCallUpdate>(updates[4]);
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

        Assert.Equal([.. a.History, .. first.Updates], b.History);
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
    public async Task AnUnknownSessionCannotBeOpenedAndUnbuiltRequestsSayWhatBringsThem()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = fixture.Sessions();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.OpenAsync("cli-nowhere", _ => { }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.QueueAsync("cli-nowhere", "later", TestContext.Current.CancellationToken));
        var steer = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            service.SteerAsync("cli-nowhere", "now", TestContext.Current.CancellationToken));
        Assert.StartsWith("Steering comes with K-3:", steer.Message, StringComparison.Ordinal);
        var question = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            service.AnswerQuestionAsync("cli-nowhere", "q-1", "yes", TestContext.Current.CancellationToken));
        Assert.StartsWith("Clarifying questions come with K-6:", question.Message, StringComparison.Ordinal);
        var rename = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            service.RenameAsync("cli-nowhere", "a title", TestContext.Current.CancellationToken));
        Assert.StartsWith("Renaming comes with G-6:", rename.Message, StringComparison.Ordinal);
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
