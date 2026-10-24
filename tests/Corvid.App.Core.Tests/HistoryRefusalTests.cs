using Corvid.App.Core.Audit;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Review;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// What Review, Rewind and Audit show when the engine refuses part of what they ask (A3 history): the reason, in the
/// place the person is looking, rather than nothing or a claim the engine cannot back.
/// </summary>
public sealed class HistoryRefusalTests
{
    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher());
    }

    [Fact]
    public async Task ReviewSaysWhyTheEditorDidNotOpenOrTheFileDidNotGoBack()
    {
        var composition = Compose("--page", "review");
        var review = new ReviewViewModel(new RefusingReview(), composition.Shell, composition.SessionHub, composition.Time);

        await review.OpenInEditorCommand.ExecuteAsync();
        Assert.Equal((Tone.Red, "No editor is configured: set $VISUAL."), (review.NoticeTone, review.Notice));

        await review.RevertFileCommand.ExecuteAsync();
        Assert.Equal((Tone.Red, "This comes with A3 history: reverting is not built on the engine yet."), (review.NoticeTone, review.Notice));
    }

    [Fact]
    public void RewindSaysWhyTheLedgerCouldNotBeRead()
    {
        var composition = Compose("--page", "rewind");
        var rewind = new RewindViewModel(new Unavailable.Rewind("Corvid cannot start: the generator is missing."), composition.Shell, composition.Time);

        Assert.True(rewind.HasSession);
        Assert.False(rewind.HasEntries);
        Assert.Equal("The checkpoint ledger could not be read: Corvid cannot start: the generator is missing.", rewind.EmptyText);
    }

    [Fact]
    public void APlannedConflictWithNoDatedEditStillSaysWhyItStopsTheRestore()
    {
        var composition = Compose("--page", "rewind");
        var rewind = new RewindViewModel(new OneConflict(), composition.Shell, composition.Time);
        rewind.Entries[0].Select.Execute(null);

        var note = Assert.Single(rewind.PlanNotes);
        Assert.Equal(Tone.Red, note.Tone);
        Assert.Equal("notes⟨U+202E⟩.md", note.FileName);
        Assert.Equal("changed since write_file created it. A restore of the files stops here and changes nothing.", note.Text);
    }

    /// <summary>A ledger of one call whose plan stops on a file the person changed, as the engine plans it (no dated edit).</summary>
    private sealed class OneConflict : IRewindService
    {
        private static readonly DateTimeOffset At = new(2026, 10, 24, 14, 0, 0, TimeSpan.Zero);

        public event EventHandler<string>? TimelineChanged { add { } remove { } }

        public Task<RewindTimeline> GetTimelineAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RewindTimeline(sessionId)
            {
                Entries = [new LedgerEntry("cp-1", LedgerEntryKind.Call, At, 1) { Tool = "write_file", AfterId = "cp-2" }],
                Turn = 1,
            });

        public Task<RestorePlan> PlanAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RestorePlan(checkpointId, At, 1)
            {
                Files = [new PutBackFile("notes‮.md", PutBackKind.Conflict, 1, 0) { Reason = "changed since write_file created it" }],
                FromTurn = 2,
                ToTurn = 1,
            });

        public Task<IReadOnlyList<RestorePreviewFile>> PreviewAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RestoreOutcome> RestoreAsync(string sessionId, string checkpointId, RewindScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task AuditShowsTheLogWhenOnlyTheEgressLedgerIsRefused()
    {
        var time = ManualTimeProvider.Demo();
        var audit = new AuditViewModel(new NoEgress(new DemoAuditService(time)), time);
        await audit.Loaded;

        Assert.False(audit.HasError);
        Assert.NotEmpty(audit.Rows);
        Assert.Equal("Select an event to read it as stored. The egress ledger comes with G-9.", audit.NoSelectionText);
    }

    [Fact]
    public async Task AuditStillFailsTheLoadWhenTheLogItselfCannotBeRead()
    {
        var time = ManualTimeProvider.Demo();
        var audit = new AuditViewModel(new Unavailable.Audit("This comes with A3 history: Audit is not built on the engine yet."), time);
        await audit.Loaded;

        Assert.True(audit.HasError);
        Assert.StartsWith("The event log could not be read: This comes with A3 history:", audit.Error, StringComparison.Ordinal);
    }

    /// <summary>One changed file whose editor and revert are refused, as the engine refuses them.</summary>
    private sealed class RefusingReview : IReviewService
    {
        public event EventHandler<string>? ChangesChanged { add { } remove { } }

        public Task<ReviewChanges> GetChangesAsync(string sessionId, ReviewScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewChanges(sessionId, scope) { Files = [new ReviewFile("a.txt", 1, 0) { NewText = "a\n", CheckpointId = "cp-1" }] });

        public Task<ReviewPoint> SendCommentsAsync(string sessionId, IReadOnlyList<ReviewComment> comments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRevertResult> RevertFileAsync(string sessionId, string path, string checkpointId, CancellationToken cancellationToken = default) =>
            Task.FromException<FileRevertResult>(new ServiceUnavailableException(Unavailable.ComesWith("history", "reverting")));

        public Task<string> OpenInEditorAsync(string path, int line, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new ServiceUnavailableException("No editor is configured: set $VISUAL."));
    }

    /// <summary>The demo's log with its egress ledger refused, as the engine refuses it until G-9.</summary>
    private sealed class NoEgress(IAuditService inner) : IAuditService
    {
        public Task<AuditLogInfo> GetLogAsync(CancellationToken cancellationToken) => inner.GetLogAsync(cancellationToken);

        public Task<IReadOnlyList<AuditSession>> ListSessionsAsync(CancellationToken cancellationToken) => inner.ListSessionsAsync(cancellationToken);

        public Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(string? sessionId, CancellationToken cancellationToken) =>
            inner.ReadEventsAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<EgressRequest>> ListEgressAsync(string? sessionId, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<EgressRequest>>(new ServiceUnavailableException("The egress ledger comes with G-9."));

        public Task<IReadOnlyList<DetectionMatch>> ListDetectionsAsync(string? sessionId, CancellationToken cancellationToken) =>
            inner.ListDetectionsAsync(sessionId, cancellationToken);

        public Task<ScanResult?> GetLastScanAsync(string? sessionId, CancellationToken cancellationToken) => inner.GetLastScanAsync(sessionId, cancellationToken);

        public Task<ScanResult> ScanAsync(string? sessionId, string failOn, CancellationToken cancellationToken) =>
            inner.ScanAsync(sessionId, failOn, cancellationToken);

        public Task<TraceExportResult> ExportAsync(string? sessionId, TraceExportOptions options, CancellationToken cancellationToken) =>
            inner.ExportAsync(sessionId, options, cancellationToken);
    }
}
