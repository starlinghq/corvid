using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Safety;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class SafetyTests
{
    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    private static string Text(IEnumerable<Models.TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    [Fact]
    public async Task TheMainSceneListsCorvidJsonsRulesAndTheSessionRuleA31Wrote()
    {
        using var app = Compose("--page", "safety");
        var safety = app.Safety;
        await safety.Loaded;

        Assert.Equal(4, safety.Rules.Count);
        Assert.Equal("4 rules · 1 session, 3 always", safety.RulesNote);
        var session = safety.Rules[0];
        Assert.Equal("session", session.Scope);
        Assert.Equal("allow", session.Decision);
        Assert.Equal("dotnet test in workspace", session.Plain);
        Assert.Contains(session.Text, run => run.Code && run.Text == "dotnet test");
        Assert.Equal("a-31 · turn 10 · cli-9f2e", Text(session.Source));
        Assert.Contains(safety.Rules, row => row.Id == DemoSafetyService.ForcePushRuleId && row.Decision == "deny" && row.Uses == "refused 1 call");
        Assert.All(safety.Rules.Skip(1), row => Assert.Equal("always", row.Scope));
    }

    [Fact]
    public async Task AnsweringThisSessionOnTheApprovalCardListsTheRuleAndSafetyRemovesIt()
    {
        using var app = Compose("--scene", "approval", "--page", "safety");
        var safety = app.Safety;
        await safety.Loaded;
        Assert.Equal(4, safety.Rules.Count);

        await app.SessionService.AnswerApprovalAsync(DemoSessionService.MainSessionId, "a-32", new ApprovalAnswer(ApprovalChoice.Session),
            TestContext.Current.CancellationToken);

        var written = Assert.Single(safety.Rules, row => row.Rule.ApprovalId == "a-32");
        Assert.Equal("session", written.Scope);
        Assert.Equal(DemoSessionService.MainSessionId, written.Rule.SessionId);
        Assert.StartsWith("a-32 · turn 13 · cli-9f2e", Text(written.Source), StringComparison.Ordinal);
        Assert.Equal("14:04", written.Written);

        written.StartRemoveCommand.Execute(null);
        Assert.True(written.IsConfirming);
        Assert.Same(written, safety.Confirming);
        Assert.StartsWith("Remove the session rule \"allow ", written.ConfirmText, StringComparison.Ordinal);
        await written.RemoveCommand.ExecuteAsync();

        Assert.DoesNotContain(safety.Rules, row => row.Rule.ApprovalId == "a-32");
        Assert.True(safety.HasNotice);
        Assert.Equal(Tone.Green, safety.NoticeTone);
        Assert.StartsWith("Removed the session rule allow ", Text(safety.Notice), StringComparison.Ordinal);
        Assert.Contains(written.Rule.Id, ((DemoSafetyService)app.SafetyService).Removed);
    }

    [Fact]
    public async Task AnAlwaysAnswerIsListedAsAlwaysWithItsSession()
    {
        using var app = Compose("--scene", "approval", "--page", "safety");
        await app.Safety.Loaded;

        await app.SessionService.AnswerApprovalAsync(DemoSessionService.MainSessionId, "a-32", new ApprovalAnswer(ApprovalChoice.Always),
            TestContext.Current.CancellationToken);

        var written = Assert.Single(app.Safety.Rules, row => row.Rule.ApprovalId == "a-32");
        Assert.Equal("always", written.Scope);
        Assert.EndsWith("cli-9f2e · corvid.json", Text(written.Source), StringComparison.Ordinal);
        app.Safety.Filter = RuleFilter.Session;
        Assert.DoesNotContain(app.Safety.Rules, row => row.Rule.ApprovalId == "a-32");
        Assert.True(app.Safety.Filters[1].IsSelected);
        app.Safety.Filter = RuleFilter.Always;
        Assert.Contains(app.Safety.Rules, row => row.Rule.ApprovalId == "a-32");
    }

    [Fact]
    public async Task OneRowAsksAtATimeAndEscCancels()
    {
        using var app = Compose("--page", "safety");
        await app.Safety.Loaded;
        var first = app.Safety.Rules[0];
        var second = app.Safety.Rules[1];

        first.StartRemoveCommand.Execute(null);
        second.StartRemoveCommand.Execute(null);
        Assert.False(first.IsConfirming);
        Assert.True(second.IsConfirming);
        Assert.True(app.Safety.CancelConfirm());
        Assert.False(second.IsConfirming);
        Assert.False(app.Safety.CancelConfirm());
    }

    [Fact]
    public async Task ARemovalTheEngineRefusesShowsARedNoticeAndKeepsTheRule()
    {
        var service = new FakeSafetyService { FailRemove = true };
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        using var safety = new SafetyViewModel(service, new FakeSession(), shell, bar, ManualTimeProvider.DemoLocal());
        await safety.Loaded;

        var row = safety.Rules[0];
        row.StartRemoveCommand.Execute(null);
        await row.RemoveCommand.ExecuteAsync();

        Assert.Equal(Tone.Red, safety.NoticeTone);
        Assert.Equal("The rule could not be removed: the rule store is read-only", Text(safety.Notice));
        Assert.Null(row.RemoveCommand.Error);
        Assert.False(row.IsConfirming);
        Assert.Single(safety.Rules);
    }

    [Fact]
    public async Task ALoadThatFailsShowsARedNotice()
    {
        var service = new FakeSafetyService { FailLoad = true };
        var bar = new StatusBarViewModel();
        using var safety = new SafetyViewModel(service, new FakeSession(), new ShellViewModel(bar), bar, ManualTimeProvider.DemoLocal());
        await safety.Loaded;

        Assert.Equal(Tone.Red, safety.NoticeTone);
        Assert.Equal("The safety settings could not be read: no answer", Text(safety.Notice));
    }

    [Fact]
    public async Task ARuleWithHiddenCharactersIsCleanedForDisplayAndRevealedOnItsConfirmation()
    {
        var service = new FakeSafetyService
        {
            Rules = [new SafetyRule("r1", RuleDecision.Allow, RuleScope.Session, "`rm -r` under `build/\u202Etxt.sh` and \U000E0041x")
            {
                SessionId = "cli-9f2e\u202E", ApprovalId = "a-1",
            }],
        };
        var bar = new StatusBarViewModel();
        using var safety = new SafetyViewModel(service, new FakeSession(), new ShellViewModel(bar), bar, ManualTimeProvider.DemoLocal());
        await safety.Loaded;

        var row = Assert.Single(safety.Rules);
        Assert.DoesNotContain('\u202E', Text(row.Text));
        Assert.DoesNotContain("\U000E0041", Text(row.Text), StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', Text(row.Source));
        Assert.True(row.HasHidden);
        Assert.Contains("⟨U+202E⟩", row.Revealed, StringComparison.Ordinal);
        Assert.Contains("⟨U+E0041⟩", row.Revealed, StringComparison.Ordinal);
        Assert.Contains("⟨U+202E⟩", row.ConfirmText, StringComparison.Ordinal);
        Assert.EndsWith("It holds hidden characters, shown in brackets.", row.ConfirmText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkupInARuleOrADetectionDoesNotRestyleIt()
    {
        var service = new FakeSafetyService
        {
            Rules = [new SafetyRule("r1", RuleDecision.Allow, RuleScope.Always, "x") { Origin = "**bold** ~muted~ `code`" }],
            Detections = [new SafetyDetection("rule`x", DetectionSeverity.High, "cli-1", DateTimeOffset.UnixEpoch, 9, [1, 2]) { Outcome = "ok\u202E" }],
        };
        var bar = new StatusBarViewModel();
        using var safety = new SafetyViewModel(service, new FakeSession(), new ShellViewModel(bar), bar, ManualTimeProvider.DemoLocal());
        await safety.Loaded;

        var source = Assert.Single(Assert.Single(safety.Rules).Source);
        Assert.Equal("**bold** ~muted~ `code`", source.Text);
        Assert.False(source.Code || source.Bold || source.Muted);
        var match = Assert.Single(safety.Matches);
        Assert.Equal("rule`x", match.RuleId);
        Assert.Equal("ok", match.Outcome);
    }

    [Fact]
    public async Task ThePackListsTwelveRulesAndTheSwitchTurnsThemToRecordingOnly()
    {
        using var app = Compose("--page", "safety");
        var safety = app.Safety;
        await safety.Loaded;

        Assert.Equal(12, safety.DetectionRules.Count);
        Assert.Equal("built-in pack · 12 rules · 5 enforce", safety.PackNote);
        Assert.Equal("enforcing", safety.Posture);
        var egress = Assert.Single(safety.DetectionRules, rule => rule.Id == "secret-read-then-egress");
        Assert.Equal("enforce", egress.Posture);
        Assert.Equal("1 match", egress.Matches);
        Assert.Equal("high", egress.Severity);
        Assert.Equal(Tone.Red, egress.SeverityTone);

        await safety.ToggleEnforceCommand.ExecuteAsync();

        Assert.False(safety.Enforce);
        Assert.Equal("recording only", safety.Posture);
        Assert.Equal("enforce · recording only", Assert.Single(safety.DetectionRules, rule => rule.Id == "secret-read-then-egress").Posture);
        Assert.All(safety.DetectionRules, rule => Assert.False(rule.ForcesAsk));
        Assert.Contains("Detections:Enforce=false", Text(safety.Notice), StringComparison.Ordinal);
        Assert.Equal("recording only · 1 match", app.StatusBar.AreaFields[0].Value);
    }

    [Fact]
    public async Task AMatchOpensAuditFilteredToItsEvents()
    {
        using var app = Compose("--page", "safety");
        await app.Safety.Loaded;

        var match = Assert.Single(app.Safety.Matches);
        Assert.Equal("14:03:49", match.At);
        Assert.Equal("events 1182, 1189", match.Events);
        Assert.Equal("1 match · it forced an ask", app.Safety.MatchesNote);
        match.OpenInAuditCommand.Execute(null);

        Assert.Equal(Area.Audit, app.Shell.CurrentArea);
        Assert.Equal(AreaLinkTarget.Detection, app.Shell.Link?.Target);
        Assert.Equal([1182L, 1189L], app.Shell.Link?.Events);
        Assert.Equal(DemoSessionService.MainSessionId, app.Shell.Link?.SessionId);
    }

    [Fact]
    public async Task TheHardlineListHasItsSixCodes()
    {
        using var app = Compose("--page", "safety");
        await app.Safety.Loaded;

        Assert.Equal(["disk_format", "raw_device_write", "recursive_delete_outside_workspace", "credential_store_write", "vault_access", "safety_configuration_change"],
            app.Safety.Hardline.Select(code => code.Code));
    }

    [Fact]
    public async Task TaintListsTheMainSessionAsTaintedByItsFetch()
    {
        using var app = Compose("--page", "safety");
        await app.Safety.Loaded;

        var first = app.Safety.Taint[0];
        Assert.Equal(DemoSessionService.MainSessionId, first.SessionId);
        Assert.True(first.IsTainted);
        Assert.Equal(Tone.Amber, first.StateTone);
        Assert.Contains("web_fetch docs.example.org", Text(first.Detail), StringComparison.Ordinal);
        Assert.All(app.Safety.Taint.Skip(1), row => Assert.Equal("clean", row.State));
    }

    [Fact]
    public async Task TheModeControlDrivesTheCurrentSessionAndTheStatusBarFollows()
    {
        using var app = Compose("--page", "safety");
        var safety = app.Safety;
        await safety.Loaded;

        Assert.True(safety.CanChangeMode);
        Assert.True(safety.Modes[1].IsSelected);
        Assert.Equal("cli-9f2e · Fix the flaky sandbox test on CI", Text(safety.ModeSession));
        Assert.Equal(ApprovalMode.Smart, app.StatusBar.Mode);

        safety.Modes[0].Choose.Execute(null);
        Assert.True(safety.Modes[0].IsSelected);
        Assert.Equal(ApprovalMode.Manual, app.StatusBar.Mode);

        safety.CycleModeCommand.Execute(null);
        Assert.Equal(ApprovalMode.Smart, app.SessionHub.Current?.Mode);
        Assert.Equal(ApprovalMode.Smart, app.StatusBar.Mode);
    }

    [Fact]
    public async Task ASessionAnotherProcessDrivesCannotHaveItsModeChangedHere()
    {
        var session = new FakeSession { ReadOnly = true };
        var bar = new StatusBarViewModel();
        using var safety = new SafetyViewModel(new FakeSafetyService(), session, new ShellViewModel(bar), bar, ManualTimeProvider.DemoLocal());
        await safety.Loaded;

        Assert.False(safety.CanChangeMode);
        Assert.False(safety.Modes[0].Choose.CanExecute(null));
        Assert.Equal("Another process drives this session: its mode is changed there.", safety.ModeNote);
        safety.CycleModeCommand.Execute(null);
        Assert.Empty(session.Requested);
    }

    [Fact]
    public async Task TheStatusBarIsHeldOnlyWhileSafetyShows()
    {
        using var app = Compose("--page", "models");
        app.StatusBar.Engine = "in-process";
        var safety = app.Safety;
        await safety.Loaded;
        Assert.Equal("in-process", app.StatusBar.Engine);

        app.Shell.Navigate(Area.Safety);
        Assert.Equal("", app.StatusBar.Engine);
        Assert.Equal("detections", app.StatusBar.AreaFields[0].Label);

        app.Shell.Navigate(Area.Skills);
        Assert.Equal("in-process", app.StatusBar.Engine);
        Assert.DoesNotContain(app.StatusBar.AreaFields, field => field.Label == "detections");
    }

    [Fact]
    public async Task DisposingTheCompositionStopsEveryWatchAndSubscription()
    {
        var time = ManualTimeProvider.DemoLocal();
        Assert.True(AppOptions.TryParse(["--page", "safety"], out var options, out _));
        var app = new AppComposition(options, new InlineDispatcher(), time);
        await app.Safety.Loaded;
        var service = (DemoSafetyService)app.SafetyService;
        Assert.True(service.Watching >= 4);

        app.Dispose();

        Assert.Equal(0, service.Watching);
        Assert.Equal(0, time.ActiveTimers);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheRulesTableIsCapped()
    {
        var service = new FakeSafetyService
        {
            Rules = [.. Enumerable.Range(0, SafetyViewModel.MaxRuleRows + 5)
                .Select(i => new SafetyRule($"r{i}", RuleDecision.Allow, RuleScope.Always, $"`cmd{i}`"))],
        };
        var bar = new StatusBarViewModel();
        using var safety = new SafetyViewModel(service, new FakeSession(), new ShellViewModel(bar), bar, ManualTimeProvider.DemoLocal());
        await safety.Loaded;

        Assert.Equal(SafetyViewModel.MaxRuleRows, safety.Rules.Count);
        Assert.Equal("and 5 more, not drawn: narrow the list with the filter", safety.HiddenRules);
    }

    [Fact]
    public async Task TheDemoServiceReadsNoSessionsWithoutAWatcher()
    {
        using var service = new DemoSafetyService(ManualTimeProvider.DemoLocal());
        var snapshot = await service.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, snapshot.Rules.Length);
        Assert.Empty(snapshot.Taint);
        Assert.Single(snapshot.Detections);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RemoveRuleAsync("nope", TestContext.Current.CancellationToken));
    }

    private sealed class FakeSafetyService : ISafetyService
    {
        public bool FailRemove { get; init; }

        public bool FailLoad { get; init; }

        public List<SafetyRule> Rules { get; init; } = [new SafetyRule("r1", RuleDecision.Allow, RuleScope.Always, "`ls`")];

        public List<SafetyDetection> Detections { get; init; } = [];

        public event EventHandler? Changed;

        public Task<SafetySnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            FailLoad
                ? Task.FromException<SafetySnapshot>(new InvalidOperationException("no answer"))
                : Task.FromResult(new SafetySnapshot([.. Rules],
                    [new DetectionPack(SafetyCatalog.BuiltInPack, true, SafetyCatalog.BuiltInRules)], [.. Detections], SafetyCatalog.Hardline, []));

        public Task RemoveRuleAsync(string ruleId, CancellationToken cancellationToken = default)
        {
            if (FailRemove)
                return Task.FromException(new InvalidOperationException("the rule store is read-only"));
            Rules.RemoveAll(rule => rule.Id == ruleId);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SetEnforceAsync(string pack, bool enforce, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSession : ISafetySession
    {
        public event EventHandler? Changed;

        public List<ApprovalMode> Requested { get; } = [];

        public string? SessionId { get; init; } = "cli-1";

        public string Title { get; init; } = "one";

        public ApprovalMode? Mode { get; set; } = ApprovalMode.Smart;

        public bool ReadOnly { get; init; }

        public IReadOnlyList<StatusTag> Tags { get; init; } = [];

        public void RequestMode(ApprovalMode mode)
        {
            Requested.Add(mode);
            Mode = mode;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
