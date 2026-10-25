using Corvid.App.Core.Audit;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class AuditViewModelTests
{
    private static async Task<AuditViewModel> Open(ShellViewModel? shell = null)
    {
        var time = ManualTimeProvider.Demo();
        var audit = new AuditViewModel(new DemoAuditService(time), time, shell);
        await audit.Loaded;
        return audit;
    }

    [Fact]
    public async Task TheStatusBarIsAuditPngs()
    {
        Assert.True(AppOptions.TryParse(["--page", "audit"], out var options, out var error), error);
        var composition = new AppComposition(options, new InlineDispatcher());
        await composition.Audit.Loaded;
        var bar = composition.StatusBar;

        Assert.Equal("claude (escalated) · cuda:0 · q0 · 410 ms", bar.Generator);
        Assert.Equal("Kev-0.8B, calibrated", bar.Decider);
        Assert.Equal(("detections", "1 high this session"), (Assert.Single(bar.AreaFields).Label, bar.AreaFields[0].Value));
        Assert.Equal(ApprovalMode.Smart, bar.Mode);
        Assert.Contains(bar.Tags, tag => tag.Text == "tainted");
        Assert.Equal(("", "", ""), (bar.Context, bar.Scheduler, bar.Branch));
        Assert.Equal("cli-9f2e", bar.SessionId);
        Assert.Equal("log 1,214 events", bar.Trailing);

        // The whole log names no session; leaving takes the detections back and the window's session comes back.
        composition.Audit.SessionIndex = composition.Audit.SessionOptions.Count - 1;
        await composition.Audit.Loaded;
        Assert.Equal("", bar.SessionId);
        Assert.Equal("1 high in the log", bar.AreaFields[0].Value);
        composition.Shell.Navigate(Area.Sessions);
        Assert.Empty(bar.AreaFields);
        Assert.Equal("38%", bar.Context);
        Assert.Equal("cli-9f2e", bar.SessionId);
    }

    [Fact]
    public async Task ItOpensAtAuditPngsMoment()
    {
        var audit = await Open();

        Assert.Equal("1,214 events · append-only ·", audit.LogCount);
        Assert.Equal("~/.local/share/corvid/events.db", audit.LogPath);
        Assert.Equal(["cli-9f2e · Fix the flaky sandbox test on CI", "cli-a410 · Draft release notes for v0.2", "cli-77b0 · Vault policy for ntfy",
            "cli-c3d2 · Spike S4 write-up", "whole log"], audit.SessionOptions.Select(option => option.Label));
        Assert.Equal((0, "cli-9f2e"), (audit.SessionIndex, audit.SessionId));
        Assert.Equal(["messages", "tool calls and results", "approvals", "decisions", "model switches", "detections", "checkpoints",
            "memory and skills", "compaction"], audit.Filters.Select(filter => filter.Label));
        Assert.Equal([true, true, true, true, true, true, false, false, false], audit.Filters.Select(filter => filter.IsChecked));

        Assert.Equal([1203L, 1202, 1201, 1196, 1194, 1191, 1190, 1189, 1182, 1180, 1177, 1170, 1168, 1166], audit.Rows.Select(row => row.Sequence));
        Assert.Equal(("1194", "14:03:52", "model_switched"), (audit.SelectedRow?.SequenceText, audit.SelectedRow?.TimeText, audit.SelectedRow?.Type));
        Assert.True(audit.SelectedRow!.IsSelected);
        Assert.Equal("newest first · any transcript row jumps here", audit.EventsNote);

        var egress = Assert.IsType<EgressView>(audit.Egress);
        Assert.Equal("Egress ledger · request 7 → claude", egress.Title);
        Assert.Equal("14:03:52 · turn 12, step 3", egress.When);
        Assert.Equal("38.2 KB · 9,410 tokens · cached prefix 6,120", egress.Sent);
        Assert.Equal("2 values", egress.RedactedTag);
        Assert.Equal("via Vault anthropic.key, api.anthropic.com", AuditText.Plain(egress.Route));
        Assert.Equal(2, egress.Lines.SelectMany(line => line.Segments).Count(segment => segment.IsRedaction));
        Assert.Null(audit.Detail);

        var detection = Assert.IsType<AuditDetectionView>(audit.Detection);
        Assert.Equal(("Detection · credential-read-then-egress", "high · enforce", "v3 · built-in pack"), (detection.Title, detection.Tag, detection.RuleDetail));
        Assert.Equal(["1182", "1189", "1190"], detection.Events.Select(link => link.Text));
        Assert.Equal("14:04 · 1 high, 0 medium · would exit 4 with --fail-on high", audit.LastScan);
    }

    [Fact]
    public async Task FiltersByGroupAndByToolPathOrType()
    {
        var audit = await Open();

        audit.Filters.Single(f => f.Group == AuditEventGroup.Checkpoints).IsChecked = true;
        Assert.Contains(audit.Rows, row => row.Type == "checkpoint_recorded");

        audit.PathFilter = "web_fetch";
        Assert.Equal([1191L, 1189, 1177], audit.Rows.Select(row => row.Sequence));

        // F21: the log narrowed to two types.
        audit.PathFilter = "tool_call_issued, approval_resolved";
        Assert.Equal(["approval_resolved", "tool_call_issued"], audit.Rows.Select(row => row.Type).Distinct().Order());
        Assert.Equal([1201L, 1191, 1189, 1180], audit.Rows.Select(row => row.Sequence));

        audit.ResetCommand.Execute(null);
        Assert.Equal("", audit.PathFilter);
        Assert.DoesNotContain(audit.Rows, row => row.Type == "checkpoint_recorded");
        Assert.Equal(14, audit.Rows.Count);
    }

    [Fact]
    public async Task ARowFilteredOutLosesTheSelection()
    {
        var audit = await Open();

        audit.Filters.Single(f => f.Group == AuditEventGroup.ModelSwitches).IsChecked = false;

        Assert.Null(audit.SelectedRow);
        Assert.Null(audit.Egress);
        Assert.True(audit.HasNoSelection);
    }

    [Fact]
    public async Task OpeningAnEventShowsItRawAndAnEgressRowShowsTheLedger()
    {
        var audit = await Open();

        audit.Rows.Single(row => row.Sequence == 1189).SelectCommand.Execute(null);

        Assert.Null(audit.Egress);
        var detail = Assert.IsType<EventDetailView>(audit.Detail);
        Assert.Equal("Event 1189 · tool_call_issued", detail.Title);
        Assert.Contains("\"url\": \"https://hooks.example.org/ci\"", detail.Json, StringComparison.Ordinal);
        Assert.Equal("cli-9f2e · Fix the flaky sandbox test on CI", AuditText.Plain(detail.Fields[1].Value));

        audit.MoveSelection(-1);
        Assert.Equal(1190, audit.SelectedRow?.Sequence);
        audit.MoveSelection(-2);
        Assert.Equal(1194, audit.SelectedRow?.Sequence);
        Assert.NotNull(audit.Egress);

        audit.Egress!.ToggleFullCommand.Execute(null);
        Assert.Equal(("Show the excerpt", "full request · 38.2 KB"), (audit.Egress.ToggleLabel, audit.Egress.ShownLabel));
        Assert.Contains("PATH=/usr/local/bin", audit.Egress.Text, StringComparison.Ordinal);

        audit.ClearSelectionCommand.Execute(null);
        Assert.Null(audit.SelectedRow);
        Assert.False(audit.ClearSelectionCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADetectionsEventLinkFiltersTheLogToItsEvents()
    {
        var audit = await Open();

        audit.Detection!.Events[0].Open.Execute(null);

        Assert.Equal([1190L, 1189, 1182], audit.Rows.Select(row => row.Sequence));
        Assert.Equal(1182, audit.SelectedRow?.Sequence);
        Assert.Equal("3 events matched by credential-read-then-egress", audit.EventsNote);

        audit.ClearMatchCommand.Execute(null);
        Assert.False(audit.HasMatch);
        Assert.Equal(14, audit.Rows.Count);
        Assert.Equal(1182, audit.SelectedRow?.Sequence);
    }

    [Fact]
    public async Task PickingAnotherSessionLoadsItsLog()
    {
        var audit = await Open();

        audit.SessionIndex = 2;
        Assert.Equal("cli-77b0", audit.SessionId);
        Assert.All(audit.Rows, row => Assert.Equal("cli-77b0", row.Event.SessionId));
        Assert.Null(audit.Detection);
        Assert.Null(audit.SelectedRow);
        Assert.Equal("Select an event to read it as stored. Nothing in this log left the machine.", audit.NoSelectionText);

        audit.SessionIndex = audit.SessionOptions.Count - 1;
        Assert.Null(audit.SessionId);
        Assert.Contains(audit.Rows, row => row.Event.SessionId == "cli-a410");
        Assert.Contains(audit.Rows, row => row.Event.SessionId == "cli-9f2e");
    }

    [Fact]
    public async Task ScanAndExportReport()
    {
        var audit = await Open();

        await audit.ScanCommand.ExecuteAsync();
        Assert.Equal("14:05 · 1 high, 0 medium · would exit 4 with --fail-on high", audit.LastScan);

        audit.ShowMetadataRetentionCommand.Execute(null);
        Assert.False(audit.FullRetention);
        await audit.ExportCommand.ExecuteAsync();
        // The demo writes nothing, and says so rather than claim a file.
        Assert.StartsWith("Demo: no file was written. The engine would write 18 events (", audit.ExportResult, StringComparison.Ordinal);
        Assert.Contains("metadata only) to ~/.local/share/corvid/traces/cli-9f2e-20261024-1405.jsonl", audit.ExportResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatLeftTheMachineLandsOnTheSessionsLatestRequest()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        var audit = await Open(shell);
        audit.SessionIndex = 1;
        Assert.Null(audit.Egress);

        shell.Follow(new AreaLink(Area.Audit, "cli-9f2e", AreaLinkTarget.Egress));
        await Task.Yield();

        Assert.Equal(Area.Audit, shell.CurrentArea);
        Assert.Equal("cli-9f2e", audit.SessionId);
        Assert.Equal(1194, audit.SelectedRow?.Sequence);
        Assert.Equal(7, audit.Egress?.Request.Number);
    }

    [Fact]
    public async Task ALinkFollowedBeforeTheAreaExistsIsWhereItOpens()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        shell.Follow(new AreaLink(Area.Audit, "cli-9f2e", AreaLinkTarget.Detection) { Events = [1182, 1189], Name = "credential-read-then-egress" });

        var audit = await Open(shell);

        Assert.True(audit.HasMatch);
        Assert.Equal([1190L, 1189, 1182], audit.Rows.Select(row => row.Sequence));
        Assert.Equal(1190, audit.SelectedRow?.Sequence);
    }

    /// <summary>F10's handoff: a detection row's "Open in Audit" in the transcript lands on the log filtered to its events.</summary>
    [Fact]
    public async Task ATranscriptDetectionOpensAuditFilteredToItsEvents()
    {
        Assert.True(AppOptions.TryParse([], out var options, out var error), error);
        var composition = new AppComposition(options, new InlineDispatcher());
        var time = ManualTimeProvider.Demo();
        var service = new CapturingSessionService(new DemoSessionService(new InlineDispatcher(), time, DemoScene.Main));
        var session = new SessionViewModel(DemoSessionService.MainSessionId, service, new InlineDispatcher(), time, composition.Shell);
        await session.OpenAsync(TestContext.Current.CancellationToken);
        service.Deliver(new DetectionMatched(new DetectionInfo("credential-read-then-egress", "high", "enforce", "a credential read, then egress")
        {
            Events = [new DetectionEvent(1182, "shell printenv"), new DetectionEvent(1189, "web_fetch POST hooks.example.org")],
            Enforced = true,
        }));
        var block = Assert.IsType<DetectionBlock>(session.Blocks.Last(b => b.Kind == TranscriptBlockKind.Detection));

        block.OpenInAuditCommand.Execute(null);
        await composition.Audit.Loaded;
        await Task.Yield();

        Assert.Equal(Area.Audit, composition.Shell.CurrentArea);
        Assert.Equal("cli-9f2e", composition.Audit.SessionId);
        Assert.Equal([1190L, 1189, 1182], composition.Audit.Rows.Select(row => row.Sequence));
        Assert.Equal("3 events matched by credential-read-then-egress", composition.Audit.EventsNote);
    }

    /// <summary>The demo's sessions, with a way to deliver an update the scenario never sends.</summary>
    internal sealed class CapturingSessionService(DemoSessionService inner) : ISessionService
    {
        private Action<SessionUpdate>? _onUpdate;

        public event EventHandler<SessionSummary>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public void Deliver(SessionUpdate update) => _onUpdate?.Invoke(update);

        /// <summary>Whether steering reaches a turn here; false stands for an engine without K-3.</summary>
        public bool Steers { get; init; } = true;

        public List<string> Steered { get; } = [];

        bool ISessionService.CanSteer => Steers;

        public Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
        {
            _onUpdate = onUpdate;
            return inner.OpenAsync(sessionId, onUpdate, cancellationToken);
        }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(sessionId, text, cancellationToken);
        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.QueueAsync(sessionId, text, cancellationToken);
        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.RemoveQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => inner.MoveQueuedAsync(sessionId, queuedId, index, cancellationToken);
        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.TakeBackQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default)
        {
            Steered.Add(text);
            return inner.SteerAsync(sessionId, text, cancellationToken);
        }
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InterruptAsync(sessionId, cancellationToken);
        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) => inner.AnswerApprovalAsync(sessionId, approvalId, answer, cancellationToken);
        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) => inner.AnswerQuestionAsync(sessionId, questionId, answer, cancellationToken);
        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) => inner.RunCommandAsync(sessionId, line, cancellationToken);
        public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default) => inner.SetApprovalModeAsync(sessionId, mode, cancellationToken);
        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => inner.SetGoalAsync(sessionId, goal, cancellationToken);
        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => inner.CompactAsync(sessionId, focus, cancellationToken);
        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => inner.ForkAsync(sessionId, throughPosition, cancellationToken);
        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => inner.RenameAsync(sessionId, title, cancellationToken);
    }
}
