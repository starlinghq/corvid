using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.State;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;
using EventData = Corvid.Abstractions.Events.EventData;
using SessionState = Corvid.App.Core.Shell.SessionState;
using UserMessage = Corvid.Abstractions.Events.UserMessage;
using TurnEnding = Corvid.App.Core.Sessions.TurnEnding;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// C-043 on the engine: the Board's marks in ui.db through the session's latest news, so any new activity brings a card
/// back with no write; Done refused while a question is open; the turn's ending and the status line in the summary; and a
/// session another process drives read as running while its log shows a turn under way.
/// </summary>
public sealed class EngineBoardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> StatusLines = new() { ["Corvid:Sessions:StatusLines"] = "true" };

    private static async Task<SessionSummary> Listed(EngineSessionService service, string id) =>
        Assert.Single(await service.ListAsync(Ct), summary => summary.Id == id);

    [Fact]
    public async Task A_done_mark_holds_until_news_and_a_turn_in_this_window_brings_the_card_back()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(
            Reply.Say("All tests pass and PR #12 is merged."), Reply.Say("Bound to POST only."))));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var changed = new List<SessionSummary>();
        service.SessionChanged += (_, summary) =>
        {
            lock (changed)
                changed.Add(summary);
        };
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "merge the PR once the tests pass", Ct);
        await service.IdleAsync();
        Assert.Equal(TurnEnding.Finished, (await Listed(service, id)).Ending);
        Assert.True(((ISessionService)service).CanMarkBoard);

        await service.SetBoardMarkAsync(id, BoardMark.Done, Ct);
        await service.IdleAsync();

        var done = await Listed(service, id);
        Assert.Equal(BoardMark.Done, done.Mark);
        Assert.NotNull(done.MarkedAt);
        lock (changed)
            Assert.Equal(BoardMark.Done, changed.Last(summary => summary.Id == id).Mark);
        var row = fixture.Runtime.UiState.BoardMarks()[id];
        Assert.Equal((UiBoardMarks.Done, fixture.Runtime.Log.GetHead(id, Ct)), (row.Mark, row.Through));
        // A rename is not news.
        await service.RenameAsync(id, "PR #12", Ct);
        await service.IdleAsync();
        Assert.Equal(BoardMark.Done, (await Listed(service, id)).Mark);

        // A message is: the card comes back with no write, and stays out once the turn ends (the summary raised then too).
        await service.SendAsync(id, "also bind it to POST only", Ct);
        await service.IdleAsync();

        Assert.Null((await Listed(service, id)).Mark);
        lock (changed)
            Assert.Null(changed.Last(summary => summary.Id == id).Mark);
        Assert.Equal(UiBoardMarks.Done, fixture.Runtime.UiState.BoardMarks()[id].Mark);

        // Parked, then Reopen clears the row.
        await service.SetBoardMarkAsync(id, BoardMark.Parked, Ct);
        await service.IdleAsync();
        Assert.Equal(BoardMark.Parked, (await Listed(service, id)).Mark);
        await service.SetBoardMarkAsync(id, null, Ct);
        await service.IdleAsync();
        Assert.Null((await Listed(service, id)).Mark);
        Assert.False(fixture.Runtime.UiState.BoardMarks().ContainsKey(id));
    }

    [Fact]
    public async Task Done_is_refused_while_a_question_is_open()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(
            Reply.Call("ask_user", """{"question":"Which issue?"}"""))),
            new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" });
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        service.QuestionsInTurn = false;
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "fix the failing issue", Ct);
        await waiter.WaitAsync<QuestionAsked>();
        await service.IdleAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetBoardMarkAsync(id, BoardMark.Done, Ct));

        Assert.Contains("Answer it first", refused.Message, StringComparison.Ordinal);
        Assert.False(fixture.Runtime.UiState.BoardMarks().ContainsKey(id));
        // Not now still parks it: it hides nothing the bell counts, since the column puts a question in Needs you anyway.
        await service.SetBoardMarkAsync(id, BoardMark.Parked, Ct);
    }

    [Fact]
    public async Task The_summary_carries_the_turns_ending_and_the_generated_status_line()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(
            Reply.Say("PR #12 is up and CI is green. Want me to merge it?"), Reply.Say("Status: PR #12 up, CI green; waits on your merge."))),
            StatusLines);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var changed = new List<SessionSummary>();
        service.SessionChanged += (_, summary) =>
        {
            lock (changed)
                changed.Add(summary);
        };
        using var subscription = await service.OpenAsync(id, _ => { }, Ct);

        await service.SendAsync(id, "open a PR for the fix", Ct);
        // The turn ends, then the status line is written on the maintenance chain, then the window reads it again.
        await service.IdleAsync();
        await fixture.Runtime.MaintenanceIdleAsync();
        await service.IdleAsync();

        var summary = await Listed(service, id);
        Assert.Equal((SessionState.Idle, TurnEnding.AsksYou), (summary.State, summary.Ending));
        Assert.Equal(("PR #12 up, CI green; waits on your merge", true), (summary.StatusLine, summary.StatusGenerated));
        // The window heard it when it was written, with no activity of its own.
        lock (changed)
            Assert.Contains(changed, raised => raised.Id == id && raised.StatusGenerated);
    }

    [Fact]
    public async Task Without_a_generated_line_the_card_reads_the_question_the_answer_ends_on()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(
            Reply.Say("I bumped Starling to 0.3.0.\n\nThe tests pass. PR #131 is up. Want me to merge it?\n\n- Directory.Packages.props\n- packages.lock.json"))));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();

        await service.SendAsync(id, "bump Starling", Ct);
        await service.IdleAsync();

        var summary = await Listed(service, id);
        Assert.Equal(TurnEnding.AsksYou, summary.Ending);
        Assert.Equal(("Want me to merge it?", false), (summary.StatusLine, summary.StatusGenerated));
    }

    [Fact]
    public async Task A_session_another_process_drives_reads_as_working_while_its_log_is_mid_turn_and_lately_moved()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("One."))));
        var other = await fixture.Host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Ct);
        await using var _ = other;
        var terminal = other.OpenSession("cli-held");
        await terminal.SendAsync("first", Ct);
        using var service = fixture.Sessions();

        var idle = await Listed(service, "cli-held");
        Assert.True(idle.ReadOnly);
        Assert.False(idle.WorkingElsewhere);

        // The terminal's next message is in the log with no answer yet: a turn is under way there. (The window's clock is
        // brought to the log's, which the other runtime stamps.)
        other.Log.Append("cli-held", other.Log.GetHead("cli-held", Ct), [EventData.Create(new UserMessage("second"))], Ct);
        var written = (await Listed(service, "cli-held")).LastActivity;
        if (written > fixture.Time.GetUtcNow())
            fixture.Time.Advance(written - fixture.Time.GetUtcNow());
        Assert.True((await Listed(service, "cli-held")).WorkingElsewhere);

        // Ten minutes with nothing written: a terminal that crashed mid-turn does not look busy forever.
        fixture.Time.Advance(EngineSessionService.WorkingElsewhereFor + TimeSpan.FromMinutes(1));
        Assert.False((await Listed(service, "cli-held")).WorkingElsewhere);

        // Marks are ui.db's, not the log's: a session a terminal drives can be put in Done.
        await service.SetBoardMarkAsync("cli-held", BoardMark.Done, Ct);
        Assert.Equal(UiBoardMarks.Done, fixture.Runtime.UiState.BoardMarks()["cli-held"].Mark);
    }
}
