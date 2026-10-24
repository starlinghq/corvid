using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Sessions;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// The session requests beside a turn: slash commands through the shared registry, the standing goal, compaction, forks,
/// and the stream's thoughts and usage.
/// </summary>
public sealed class EngineSessionRequestTests
{
    private const string Answer = "You have two notes: buy milk and call Ada.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeModels NotesTurn() => new(new FakeGenerator(Reply.Call("list_notes"), Reply.Say(Answer)));

    [Fact]
    public async Task SlashCommandsRunThroughTheSharedRegistryAndTheirOutputIsACard()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        // The palette offers the registry's commands, without leaving, which is the window's business.
        var offered = Assert.Single(subscription.History.OfType<AvailableCommandsUpdate>()).Commands;
        Assert.Contains(offered, command => command.Name == "goal" && command.Usage == "[text | clear]");
        Assert.DoesNotContain(offered, command => command.Name is "exit" or "quit");

        await service.RunCommandAsync(id, "/help", Ct);
        var help = Assert.Single(waiter.Updates.OfType<CommandResult>());
        Assert.Equal(("/help", "Commands", 3), (help.Line, help.Title, help.Columns.Length));
        Assert.Contains(help.Rows, row => row[0] == "/goal");

        await service.RunCommandAsync(id, "/goal ship the release", Ct);
        Assert.Equal("ship the release", waiter.Updates.OfType<GoalChanged>().Single().Goal);

        await service.RunCommandAsync(id, "/nope", Ct);
        Assert.Equal(OutputTone.Error, waiter.Updates.OfType<CommandResult>().Last().Tone);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RunCommandAsync(id, "/tmp/file is a path", Ct));
    }

    [Fact]
    public async Task TheGoalIsWrittenToTheLogAndComesBackWithTheSession()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using (var service = fixture.Sessions())
        {
            var waiter = new SessionWaiter();
            using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
            await service.SetGoalAsync(id, "  keep the tests green  ", Ct);
            Assert.Equal("keep the tests green", waiter.Updates.OfType<GoalChanged>().Single().Goal);
        }

        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, Ct);
        Assert.Equal("keep the tests green", SessionFold.Replay(id, reopened.History).Goal);

        var heard = new SessionWaiter();
        using var again = await fresh.OpenAsync(id, heard.Add, Ct);
        await fresh.SetGoalAsync(id, " ", Ct);
        Assert.Null(heard.Updates.OfType<GoalChanged>().Single().Goal);
    }

    [Fact]
    public async Task CompactingAShortSessionSaysThereIsNothingToCompact()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "what are my notes?", Ct);
        await service.IdleAsync();

        await service.CompactAsync(id, "the notes", Ct);

        var notice = Assert.Single(waiter.Updates.OfType<Notice>());
        Assert.Equal((NoticeKind.Info, "Nothing to compact yet."), (notice.Kind, notice.Text));
        Assert.StartsWith("nothing to compact", notice.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForkAtAMessageHoldsWhatCameBeforeItAndAForkAtTheHeadHoldsEverything()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "what are my notes?", Ct);
        await service.IdleAsync();
        var message = waiter.Updates.OfType<UserMessage>().Single();

        // "Edit and resend" on the first message: nothing came before it, so the fork is a new, empty session.
        var empty = await service.ForkAsync(id, message.Position, Ct);
        await service.IdleAsync();
        using (var opened = await service.OpenAsync(empty, _ => { }, Ct))
        {
            var view = SessionFold.Replay(empty, opened.History);
            Assert.Empty(view.Transcript);
            Assert.Equal((id, 0), (view.ForkOf, view.ForkTurn));
        }

        var whole = await service.ForkAsync(id, null, Ct);
        await service.IdleAsync();
        using (var opened = await service.OpenAsync(whole, _ => { }, Ct))
        {
            var view = SessionFold.Replay(whole, opened.History);
            Assert.Contains(view.Transcript, entry => entry is UserEntry { Text: "what are my notes?" });
            Assert.Equal((id, 1), (view.ForkOf, view.ForkTurn));
        }

        var listed = await service.ListAsync(Ct);
        var blank = Assert.Single(listed, summary => summary.Id == empty);
        Assert.Equal(("New session", id), (blank.Title, blank.ForkOf));
        var copy = Assert.Single(listed, summary => summary.Id == whole);
        Assert.Equal(("what are my notes?", 1, id, 1), (copy.Title, copy.Turns, copy.ForkOf, copy.ForkTurn));
        Assert.StartsWith(EngineSessionService.Surface + "-", whole, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTranslatorCarriesThoughtsUsageAndTheStepBudget()
    {
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var translator = new SessionUpdateTranslator(Path.GetTempPath()) { Model = "fake-gen", MaxSteps = 6, ContextWindow = 8192 };

        var started = translator.Translate(new PromptSent("hi", 4) { At = at });
        Assert.Equal(6, Assert.IsType<TurnStarted>(started[1]).MaxSteps);
        Assert.Equal(new AgentThoughtChunk("pondering") { At = at }, Assert.Single(translator.Translate(new ThoughtChunk("pondering") { At = at })));
        var usage = Assert.IsType<UsageUpdate>(Assert.Single(translator.Translate(new UsageReported(42, 7) { At = at })));
        Assert.Equal((49, 42, 8192), (usage.TurnTokens, usage.ContextTokens, usage.ContextWindow));
        var more = Assert.IsType<UsageUpdate>(Assert.Single(translator.Translate(new UsageReported(60, 5) { At = at })));
        Assert.Equal((114, 60), (more.TurnTokens, more.ContextTokens));

        var ended = Assert.IsType<TurnEnded>(Assert.Single(translator.EndTurn(TurnOutcome.Answered, at)));
        Assert.Equal(114, ended.Tokens);
        Assert.Equal((0, 1, 1), (translator.TurnAt(3), translator.TurnAt(4), translator.TurnAt(99)));
    }

    [Fact]
    public async Task ALiveTurnReportsItsTokensAndTheTurnHeaderKeepsThem()
    {
        await using var fixture = await EngineFixture.StartAsync(NotesTurn());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "what are my notes?", Ct);
        await service.IdleAsync();

        // Two generator calls of 42 + 7 tokens each, as the fake provider reports them, against fake-gen's 8,192.
        var usage = waiter.Updates.OfType<UsageUpdate>().Last();
        Assert.Equal((98, 42, 8192), (usage.TurnTokens, usage.ContextTokens, usage.ContextWindow));
        Assert.Equal(98, waiter.Updates.OfType<TurnEnded>().Single().Tokens);
        Assert.Equal(fixture.Runtime.Options.Turn.MaxSteps, waiter.Updates.OfType<TurnStarted>().Single().MaxSteps);
    }
}
