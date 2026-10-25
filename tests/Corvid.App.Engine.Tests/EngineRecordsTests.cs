using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using LoggedTitle = Corvid.Abstractions.Events.SessionTitled;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A4's records on the engine: generated titles and renames (G-6), pins and drafts in ui.db (G-7), recaps on request and
/// on return, and the composer's # and ! lines.
/// </summary>
public sealed class EngineRecordsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> Titles = new() { ["Corvid:Sessions:Titles"] = "true" };

    [Fact]
    public async Task The_local_generator_titles_a_session_after_its_first_exchange_and_the_page_and_the_rail_follow()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello."), Reply.Say("Greeting exchange")));
        await using var fixture = await EngineFixture.StartAsync(models, Titles);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var summaries = new List<SessionSummary>();
        service.SessionChanged += (_, summary) =>
        {
            lock (summaries)
                summaries.Add(summary);
        };
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "hi there", Ct);
        var titled = await waiter.WaitAsync<TitleChanged>(title => title.Generated);
        await service.IdleAsync();

        Assert.Equal("Greeting exchange", titled.Title);
        Assert.Equal("Greeting exchange", Assert.Single(await service.ListAsync(Ct), summary => summary.Id == id).Title);
        lock (summaries)
            Assert.Contains(summaries, summary => summary.Id == id && summary.Title == "Greeting exchange");
    }

    [Fact]
    public async Task A_rename_is_logged_heads_the_open_session_and_survives_a_replay()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using (await service.OpenAsync(id, waiter.Add, Ct))
        {
            await service.SendAsync(id, "plan the release", Ct);
            await service.IdleAsync();

            await service.RenameAsync(id, "Release v0.2", Ct);
            await service.IdleAsync();

            Assert.Equal("Release v0.2", (await waiter.WaitAsync<TitleChanged>(title => title.Title == "Release v0.2")).Title);
        }
        await service.IdleAsync();

        var logged = fixture.Runtime.Log.Read(id, cancellationToken: Ct).Select(item => item.Payload).OfType<LoggedTitle>().Single();
        Assert.Equal(("Release v0.2", "user"), (logged.Title, logged.Source));
        // Opened again (the channel was let go): the replay heads it with the rename.
        using var again = new SessionViewModel(id, service, new InlineDispatcher(), fixture.Time);
        await again.OpenAsync(Ct);
        Assert.Equal("Release v0.2", again.Title);
    }

    [Fact]
    public async Task A_pin_from_the_window_or_the_cli_heads_the_rail()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var first = fixture.Runtime.OpenSession(surface: "app");
        await first.SendAsync("one", Ct);
        var second = fixture.Runtime.OpenSession(surface: "cli");
        await second.SendAsync("two", Ct);
        using var service = fixture.Sessions();
        var changed = new List<SessionSummary>();
        service.SessionChanged += (_, summary) =>
        {
            lock (changed)
                changed.Add(summary);
        };

        await service.SetPinnedAsync(first.Id, true, Ct);
        await service.IdleAsync();
        // The CLI's /pin writes the same ui.db.
        fixture.Runtime.UiState.SetPinned(second.Id, true, DateTimeOffset.UtcNow);

        var listed = await service.ListAsync(Ct);
        Assert.True(Assert.Single(listed, summary => summary.Id == first.Id).Pinned);
        Assert.True(Assert.Single(listed, summary => summary.Id == second.Id).Pinned);
        lock (changed)
            Assert.Contains(changed, summary => summary.Id == first.Id && summary.Pinned);
        Assert.True(((ISessionService)service).CanPin);
    }

    [Fact]
    public async Task A_draft_is_kept_in_ui_db_after_the_composer_is_still_and_comes_back_in_the_next_window()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        using (var view = new SessionViewModel(id, service, new InlineDispatcher(), fixture.Time))
        {
            await view.OpenAsync(Ct);
            view.Draft = "and then check the";
            view.Draft = "and then check the changelog";
            // Nothing is written per keystroke.
            Assert.Null(fixture.Runtime.UiState.GetDraft(id));
            fixture.Time.Advance(SessionViewModel.DraftSaveDelay);
            await service.IdleAsync();
            Assert.Equal("and then check the changelog", fixture.Runtime.UiState.GetDraft(id)!.Text);
            view.StashDraftCommand.Execute(null);
            fixture.Time.Advance(SessionViewModel.DraftSaveDelay);
            await service.IdleAsync();
        }
        Assert.Equal(("", "and then check the changelog"), (fixture.Runtime.UiState.GetDraft(id)!.Text, fixture.Runtime.UiState.GetDraft(id)!.Stash));

        // The next window (a restart) finds it put aside.
        using var next = new SessionViewModel(id, service, new InlineDispatcher(), fixture.Time);
        await next.OpenAsync(Ct);
        Assert.True(next.HasStashedDraft);
        Assert.Equal("draft saved", next.DraftNote);
    }

    [Fact]
    public async Task A_memory_line_is_staged_with_a_notice_and_never_reaches_the_model()
    {
        var models = new FakeModels();
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "# the release branch is main", Ct);

        var notice = await waiter.WaitAsync<Notice>(item => item.Kind == NoticeKind.MemoryStaged);
        Assert.StartsWith("Staged for review: \"the release branch is main\"", notice.Text, StringComparison.Ordinal);
        Assert.Contains(fixture.Runtime.Memory!.Writer!.ListPending(), pending => pending.PendingId == notice.Target);
        Assert.DoesNotContain(fixture.Runtime.Log.Read(id, cancellationToken: Ct), item => item.Payload is LoggedUserMessage);
        Assert.Empty(models.Generator.Calls);
        Assert.True(((ISessionService)service).HasComposerModes);
    }

    /// <summary>
    /// A4V2-3: a <c>#</c> line staged in the window tells the Memory service, which tells the Board's tray and the rail's
    /// Memory badge to count again, on the UI thread; nothing is raised before the write.
    /// </summary>
    [Fact]
    public async Task A_memory_line_tells_the_memory_page_and_the_board_to_count_again()
    {
        await using var fixture = await EngineFixture.StartAsync(new FakeModels());
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var ui = new QueuedDispatcher();
        using var memory = new Engine.Memory.EngineMemoryService(fixture.Engine, fixture.Time, dispatcher: ui);
        var dispatcher = new InlineDispatcher();
        using var mcp = new Engine.Mcp.EngineMcpService(fixture.Engine, dispatcher, fixture.Time);
        using var gateway = new Engine.Gateway.EngineGatewayService(fixture.Engine, dispatcher, fixture.Time);
        using var jobs = new Engine.Jobs.EngineJobsService(fixture.Engine, dispatcher, fixture.Time);
        using var board = new Engine.Board.EngineBoardService(fixture.Engine, mcp, gateway, jobs, memory, dispatcher, fixture.Time);
        var (memoryChanged, trayChanged) = (0, 0);
        memory.Changed += (_, _) => memoryChanged++;
        board.AttentionChanged += (_, _) => trayChanged++;
        Assert.Empty((await memory.LoadAsync(Ct)).StagedFacts);

        await service.SendAsync(id, "# my deploy target is staging-2", Ct);
        await service.IdleAsync();
        Assert.Equal((0, 0), (memoryChanged, trayChanged));
        ui.RunAll();

        Assert.Equal((1, 1), (memoryChanged, trayChanged));
        Assert.Single((await memory.LoadAsync(Ct)).StagedFacts);
        var tray = await board.ListAttentionAsync(Ct);
        Assert.Contains(tray, item => item.Kind == Core.Board.AttentionKind.Memory);
    }

    /// <summary>A UI thread that is elsewhere: what is posted waits until the test runs it.</summary>
    private sealed class QueuedDispatcher : IDispatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _queue = new();

        public bool HasThreadAccess => false;

        public bool Post(Action action)
        {
            _queue.Enqueue(action);
            return true;
        }

        public void RunAll()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }
    }

    [Fact]
    public async Task A_shell_line_asks_on_the_card_and_a_denial_runs_nothing()
    {
        var models = new FakeModels();
        await using var fixture = await EngineFixture.StartAsync(models, new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" });
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "!rm -rf build", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        Assert.Equal("shell", request.Tool);
        Assert.Equal("rm", Assert.Single(request.Commands).Verb);
        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Deny, Ct);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        var view = SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]);
        Assert.Equal("!rm -rf build", Assert.Single(view.Transcript.OfType<UserEntry>()).Text);
        var card = Assert.Single(view.Transcript.OfType<ToolEntry>());
        Assert.Equal(("shell", ToolCallStatus.Failed), (card.Tool, card.Status));
        Assert.Empty(models.Generator.Calls);
    }

    [Fact]
    public async Task A_recap_on_request_arrives_as_a_notice_and_one_comes_on_return_after_an_absence()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Planned."), Reply.Say("You planned the release."), Reply.Say("Checked."),
            Reply.Say("You came back.")));
        await using var fixture = await EngineFixture.StartAsync(models, new Dictionary<string, string?> { ["Corvid:Sessions:RecapAfterMinutes"] = "60" });
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "plan the release", Ct);
        await service.IdleAsync();

        await service.RecapAsync(id, Ct);
        var recap = await waiter.WaitAsync<Notice>(item => item.Kind == NoticeKind.Recap);
        Assert.Equal(("Recap", "You planned the release."), (recap.Text, recap.Detail));
        await service.RecapAsync(id, Ct);
        await waiter.WaitAsync<Notice>(item => item.Text == "Nothing happened since the last recap.");

        // Last seen two hours ago, and a turn since the recap: coming back recaps it.
        await fixture.Runtime.SendAsyncFor(id, "and the changelog");
        fixture.Runtime.UiState.MarkSeen(id, 0, fixture.Time.GetUtcNow().AddHours(-2));
        await service.ViewedAsync(id, viewing: true, Ct);
        await service.IdleAsync();

        var back = await waiter.WaitAsync<Notice>(item => item.Kind == NoticeKind.Recap && item.Text == "While you were away");
        Assert.Equal("You came back.", back.Detail);
        Assert.Equal(fixture.Time.GetUtcNow(), fixture.Runtime.UiState.LastSeen(id)!.At);
        // Seen just now: looking again does not recap.
        await service.ViewedAsync(id, viewing: false, Ct);
        await service.ViewedAsync(id, viewing: true, Ct);
        await service.IdleAsync();
        Assert.Single(waiter.Updates.OfType<Notice>(), item => item.Text == "While you were away");
    }
}

internal static class RuntimeTurns
{
    /// <summary>A turn in the session from another surface of this process (the CLI's way), waited for.</summary>
    public static Task SendAsyncFor(this Corvid.Host.Composition.CorvidRuntime runtime, string sessionId, string text) =>
        runtime.OpenSession(sessionId, "cli").SendAsync(text, TestContext.Current.CancellationToken);
}
