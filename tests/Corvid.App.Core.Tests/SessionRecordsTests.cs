using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// A4's records in the view models: the draft kept through the service (ui.db on the engine, G-7) without a write per
/// keystroke, the pin in the header and the rail, and the hub telling the service which session the person looks at.
/// </summary>
public sealed class SessionRecordsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The demo's sessions with a draft store and a record of what the hub said was viewed.</summary>
    private sealed class KeepingService(DemoSessionService inner) : ISessionService
    {
        public Dictionary<string, SessionDraft> Drafts { get; } = new(StringComparer.Ordinal);

        public List<SessionDraft> Saves { get; } = [];

        public List<(string Id, bool Viewing)> Viewed { get; } = [];

        public event EventHandler<SessionSummary>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public Task<SessionDraft?> LoadDraftAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Drafts.TryGetValue(sessionId, out var draft) ? draft : null);

        public Task SaveDraftAsync(string sessionId, SessionDraft draft, CancellationToken cancellationToken = default)
        {
            Saves.Add(draft);
            Drafts[sessionId] = draft;
            return Task.CompletedTask;
        }

        public Task ViewedAsync(string sessionId, bool viewing, CancellationToken cancellationToken = default)
        {
            Viewed.Add((sessionId, viewing));
            return Task.CompletedTask;
        }

        public bool CanPin => inner.CanPin;

        public Task SetPinnedAsync(string sessionId, bool pinned, CancellationToken cancellationToken = default) =>
            inner.SetPinnedAsync(sessionId, pinned, cancellationToken);

        /// <summary>Sent to the view right after it opens, as a live update (a question the model asked, say).</summary>
        public SessionUpdate? AfterOpen { get; set; }

        public List<(string QuestionId, string Answer)> Answers { get; } = [];

        public List<string> Recaps { get; } = [];

        public bool CanRecap => true;

        public Task RecapAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Recaps.Add(sessionId);
            return Task.CompletedTask;
        }

        public async Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
        {
            var subscription = await inner.OpenAsync(sessionId, onUpdate, cancellationToken);
            if (AfterOpen is { } update)
                onUpdate(update);
            return subscription;
        }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        /// <summary>When set, sending or queueing is refused with this reason, as the engine refuses a <c>#</c> line with no extractor.</summary>
        public string? Refuse { get; set; }

        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
            Refuse is { } reason ? Task.FromException(new InvalidOperationException(reason)) : inner.SendAsync(sessionId, text, cancellationToken);
        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) =>
            Refuse is { } reason ? Task.FromException<QueuedMessage>(new InvalidOperationException(reason)) : inner.QueueAsync(sessionId, text, cancellationToken);
        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.RemoveQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => inner.MoveQueuedAsync(sessionId, queuedId, index, cancellationToken);
        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.TakeBackQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SteerAsync(sessionId, text, cancellationToken);
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InterruptAsync(sessionId, cancellationToken);
        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) => inner.AnswerApprovalAsync(sessionId, approvalId, answer, cancellationToken);
        /// <summary>When set, an answer is refused with this reason, as the engine refuses one to a question answered elsewhere.</summary>
        public string? RefuseAnswer { get; set; }

        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default)
        {
            Answers.Add((questionId, answer));
            return RefuseAnswer is { } reason ? Task.FromException(new InvalidOperationException(reason)) : Task.CompletedTask;
        }
        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) => inner.RunCommandAsync(sessionId, line, cancellationToken);
        public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default) => inner.SetApprovalModeAsync(sessionId, mode, cancellationToken);
        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => inner.SetGoalAsync(sessionId, goal, cancellationToken);
        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => inner.CompactAsync(sessionId, focus, cancellationToken);
        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => inner.ForkAsync(sessionId, throughPosition, cancellationToken);
        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => inner.RenameAsync(sessionId, title, cancellationToken);
    }

    private static (KeepingService Service, ManualTimeProvider Time, DemoSessionService Demo) Start()
    {
        var time = ManualTimeProvider.Demo();
        var demo = new DemoSessionService(new InlineDispatcher(), time, DemoScene.Main);
        return (new KeepingService(demo), time, demo);
    }

    [Fact]
    public async Task An_option_with_hidden_characters_shows_them_and_sends_only_its_visible_text()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        // The model wrote the options: tag characters after "Blue" spell "also"; the second is only a zero-width space.
        const string Blue = "Blue\U000E0061\U000E006C\U000E0073\U000E006F";
        service.AfterOpen = new QuestionAsked("q-hidden", "Which colour?") { Options = [Blue, "​", "Green"] };
        using var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);

        var question = Assert.IsType<QuestionBlock>(view.PendingQuestion);
        // Revealed, not dropped: the person sees the option holds more than its word.
        Assert.Equal(["Blue⟨U+E0061⟩⟨U+E006C⟩⟨U+E0073⟩⟨U+E006F⟩", "⟨U+200B⟩", "Green"], question.Options.Select(option => option.Label));
        // Nothing visible: it cannot be chosen.
        Assert.False(question.Options[1].Choose.CanExecute(null));

        question.Options[0].Choose.Execute(null);
        question.Options[2].Choose.Execute(null);

        // Only what a reader sees goes as the person's answer.
        Assert.Equal([("q-hidden", "Blue"), ("q-hidden", "Green")], service.Answers);
    }

    [Fact]
    public async Task An_answer_the_service_refuses_goes_back_to_the_composer()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        service.AfterOpen = new QuestionAsked("q-open", "Which branch?");
        service.RefuseAnswer = "That question is no longer open; nothing was sent.";
        using var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);
        Assert.True(view.PendingQuestion!.AllowsFreeText);

        view.Draft = "the release branch";
        view.SubmitCommand.Execute(null);

        // M1: answered from another view meanwhile (or a line the engine will not take as an answer): the words stay.
        Assert.Equal([("q-open", "the release branch")], service.Answers);
        Assert.Equal("the release branch", view.Draft);
        Assert.Equal("That question is no longer open; nothing was sent.", view.Error);
    }

    [Fact]
    public async Task A_slash_command_typed_while_a_question_is_open_runs_and_is_not_its_answer()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        service.AfterOpen = new QuestionAsked("q-open", "Which branch?");
        using var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);

        view.Draft = "/help";
        view.SubmitCommand.Execute(null);

        Assert.Empty(service.Answers);
        Assert.True(view.PendingQuestion!.IsOpen);
    }

    [Fact]
    public async Task A_read_only_view_cannot_recap_or_rename_the_session_another_process_drives()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        using var view = new SessionViewModel(DemoSessionService.TerminalSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);

        Assert.True(view.IsReadOnly);
        // The service writes recaps, but not here: the session's log is the driving process's (G-12).
        Assert.True(view.CanRecap);
        Assert.False(view.RecapCommand.CanExecute(null));
        Assert.False(view.RenameCommand.CanExecute("A new name"));

        using var writable = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await writable.OpenAsync(Ct);
        Assert.True(writable.RecapCommand.CanExecute(null));
        Assert.True(writable.RenameCommand.CanExecute("A new name"));
        writable.RecapCommand.Execute(null);
        Assert.Equal([DemoSessionService.VaultSessionId], service.Recaps);
    }

    [Fact]
    public async Task A_kept_draft_comes_back_on_open_and_typing_is_written_once_the_composer_is_still()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        service.Drafts[DemoSessionService.VaultSessionId] = new SessionDraft("half a thought", "put aside");
        using var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);

        await view.OpenAsync(Ct);

        Assert.Equal("half a thought", view.Draft);
        Assert.True(view.HasStashedDraft);
        // Opening wrote nothing back.
        time.Advance(SessionViewModel.DraftSaveDelay);
        Assert.Empty(service.Saves);

        view.Draft = "half a thought, and";
        time.Advance(SessionViewModel.DraftSaveDelay / 2);
        view.Draft = "half a thought, and more";
        time.Advance(SessionViewModel.DraftSaveDelay / 2);
        Assert.Empty(service.Saves);
        time.Advance(SessionViewModel.DraftSaveDelay);

        Assert.Equal(new SessionDraft("half a thought, and more", "put aside"), Assert.Single(service.Saves));
    }

    [Fact]
    public async Task A_refused_message_keeps_its_reason_on_screen_after_its_restored_draft_is_written()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        using var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);
        service.Refuse = "Memory writes need the extractor slot; bind one to stage and review writes.";

        view.Draft = "# the build uses dotnet 10";
        view.SubmitCommand.Execute(null);

        // The line comes back to the composer with the reason it was refused.
        Assert.Equal("# the build uses dotnet 10", view.Draft);
        Assert.Equal(service.Refuse, view.Error);
        // Keeping the restored draft is not a new request: the reason stays until the person sends again.
        time.Advance(SessionViewModel.DraftSaveDelay);
        Assert.Equal("# the build uses dotnet 10", service.Saves[^1].Text);
        Assert.Equal(service.Refuse, view.Error);
    }

    [Fact]
    public async Task Closing_the_window_writes_a_draft_still_waiting_for_its_delay()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        var view = new SessionViewModel(DemoSessionService.VaultSessionId, service, new InlineDispatcher(), time);
        await view.OpenAsync(Ct);

        view.Draft = "unsent";
        view.Dispose();

        Assert.Equal("unsent", Assert.Single(service.Saves).Text);
    }

    [Fact]
    public async Task The_header_pins_a_session_and_the_rail_moves_it_to_pinned()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        using var hub = new SessionHub(service, dispatcher, time, shell, bar);
        await hub.StartAsync(DemoSessionService.VaultSessionId, null, Ct);
        var view = hub.Current!;
        Assert.False(view.IsPinned);

        view.TogglePinCommand.Execute(null);

        Assert.True(view.IsPinned);
        Assert.Equal("Unpin from the rail", view.PinLabel);
        Assert.Contains(shell.SessionGroups.Single(group => group.Title == "Pinned").Sessions, item => item.Id == DemoSessionService.VaultSessionId);
    }

    [Fact]
    public async Task The_hub_tells_the_service_which_session_the_person_looks_at()
    {
        var (service, time, demo) = Start();
        using var _ = demo;
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        using (var hub = new SessionHub(service, new InlineDispatcher(), time, shell, bar))
        {
            await hub.StartAsync(DemoSessionService.VaultSessionId, null, Ct);
            shell.SelectSession(shell.FindSession(DemoSessionService.MainSessionId)!);
        }

        Assert.Equal([(DemoSessionService.VaultSessionId, true), (DemoSessionService.VaultSessionId, false), (DemoSessionService.MainSessionId, true),
            (DemoSessionService.MainSessionId, false)], service.Viewed);
    }
}
