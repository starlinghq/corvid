using System.Collections.Specialized;
using Corvid.App.Core.Audit;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Audit with untrusted text in it (what left the machine, the raw event, summaries, names), the filter's cost, links that
/// land on what they name, and the trace's file name.
/// </summary>
public sealed class AuditReviewTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 14, 3, 52, TimeSpan.Zero);

    private static async Task<AuditViewModel> Open(IAuditService? service = null, ShellViewModel? shell = null)
    {
        var time = ManualTimeProvider.Demo();
        var audit = new AuditViewModel(service ?? new DemoAuditService(time), time, shell);
        await audit.Loaded;
        return audit;
    }

    private static string Shown(AuditLine line) => string.Concat(line.Segments.Select(segment => segment.Text));

    [Fact]
    public void WhatLeftTheMachineShowsEveryHiddenCharacterAndCopiesTheRawText()
    {
        var full = "zero\u200Bwidth\n" + "tag\U000E0041char\n" + "\u202Eevil\r\n" + "GH_TOKEN=[redacted:credential]\n" + "joined \U0001F469\u200D\U0001F466 kept, stray \u200D";
        var request = new EgressRequest(1, 10, "s", At, "model") { Excerpt = full, Full = full, Redacted = 1 };

        var egress = new EgressView(request, TimeZoneInfo.Utc);

        Assert.Equal(["zero⟨U+200B⟩width", "tag⟨U+E0041⟩char", "⟨U+202E⟩evil", "GH_TOKEN=[redacted:credential]", "joined \U0001F469\u200D\U0001F466 kept, stray ⟨U+200D⟩"],
            egress.Lines.Select(Shown));
        Assert.True(egress.Lines[3].Segments[1].IsRedaction);
        // Copy keeps what was sent, nothing revealed or removed.
        Assert.Equal(full, egress.Text);
        Assert.False(egress.HasRedactionMismatch);
    }

    [Fact]
    public void OnlyTheGatesMarkerFormCountsAndAMismatchedCountIsFlagged()
    {
        Assert.Equal([new RedactedSegment("a [redacted:Not A Marker] b", false)], AuditText.Redactions("a [redacted:Not A Marker] b"));
        Assert.Equal([new RedactedSegment("[redacted:api_key]", true)], AuditText.Redactions("[redacted:api_key]"));

        // A tool printed a marker of its own: the text holds two, the gate redacted one.
        var full = "GH_TOKEN=[redacted:credential]\ntool output: [redacted:email]";
        var egress = new EgressView(new EgressRequest(1, 10, "s", At, "model") { Excerpt = full, Full = full, Redacted = 1 }, TimeZoneInfo.Utc);

        Assert.True(egress.HasRedactionMismatch);
        Assert.StartsWith("The request as sent holds 2 redaction markers but the gate recorded 1", egress.RedactionMismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGatesPlaceholdersAreMarkedAndARepeatedValueCountsOnce()
    {
        Assert.Equal([new RedactedSegment("to ", false), new RedactedSegment("<PERSON_1>", true), new RedactedSegment(" at ", false),
            new RedactedSegment("<EMAIL_1>", true)], AuditText.Redactions("to <PERSON_1> at <EMAIL_1>"));
        // Not the gate's form: lower case, no number, or the neutralized ‹…› of quoted placeholder text.
        Assert.Equal([new RedactedSegment("<person_1> <EMAIL> ‹EMAIL_1›", false)], AuditText.Redactions("<person_1> <EMAIL> ‹EMAIL_1›"));

        // One person named three times and one address: two values, as the gate counts them.
        var full = "[user] Write to <PERSON_1> at <EMAIL_1>.\n[assistant] Writing to <PERSON_1>.\n[user] Thank <PERSON_1>.";
        Assert.Equal(2, AuditText.CountRedactions(full));
        var egress = new EgressView(new EgressRequest(1, 10, "s", At, "model") { Excerpt = full, Full = full, Redacted = 2 }, TimeZoneInfo.Utc);
        Assert.False(egress.HasRedactionMismatch);
        Assert.Equal(4, egress.Lines.SelectMany(line => line.Segments).Count(segment => segment.IsRedaction));

        // A request sent as it was whose text holds a placeholder-shaped string: the gate redacted nothing, and it is flagged.
        var sent = new EgressView(new EgressRequest(2, 11, "s", At, "model") { Full = "a page quoting <EMAIL_1>", Redacted = 0 }, TimeZoneInfo.Utc);
        Assert.True(sent.HasRedactionMismatch);
    }

    [Fact]
    public void ADirectRequestNamesItsHostAndUnknownTokensAreLeftOut()
    {
        var local = new EgressView(new EgressRequest(1, 10, "s", At, "m") { Host = "127.0.0.1:8080", Bytes = 812 }, TimeZoneInfo.Utc);
        Assert.Equal("direct to 127.0.0.1:8080", AuditText.Plain(local.Route));
        Assert.Equal("812 B", local.Sent);

        var cloud = new EgressView(new EgressRequest(1, 10, "s", At, "m") { Secret = "k", Host = "h", Bytes = 38_200, Tokens = 9410, CachedPrefix = 6120 },
            TimeZoneInfo.Utc);
        Assert.Equal("38.2 KB · 9,410 tokens · cached prefix 6,120", cloud.Sent);
    }

    [Fact]
    public void TheRawEventShowsHiddenCharactersAndTheNamesAreCleaned()
    {
        var auditEvent = new AuditEvent(5, "s-1", At, "tool\u202E_result", AuditEventGroup.Tools)
        {
            Json = "{\r\n  \"text\": \"over\u202Eride\"\r\n}",
        };

        var detail = new EventDetailView(auditEvent, TimeZoneInfo.Utc, "s-1 · title\U000E0041");
        var row = new AuditEventRow(auditEvent, TimeZoneInfo.Utc, _ => { });

        Assert.Equal(["{", "  \"text\": \"over⟨U+202E⟩ride\"", "}"], detail.Lines.Select(Shown));
        Assert.Equal("Event 5 · tool_result", detail.Title);
        Assert.Equal("s-1 · title", AuditText.Plain(detail.Fields[1].Value));
        Assert.Equal("tool_result", row.Type);

        var match = new DetectionMatch("rule\u202E-x", 1, "high\u200B", "enforce", 5, [4]) { Pack = "pack\U000E0041" };
        var detection = new AuditDetectionView(match, []);
        Assert.Equal(("Detection · rule-x", "high · enforce", "v1 · pack", "rule-x"), (detection.Title, detection.Tag, detection.RuleDetail, detection.Rule));
        var egress = new EgressView(new EgressRequest(1, 10, "s", At, "claude\u202E") { Host = "api\u200B.example", Secret = "key`x" }, TimeZoneInfo.Utc);
        Assert.Equal("claude", egress.Model);
        Assert.Equal([new AuditSpan("via Vault "), new AuditSpan("key`x", AuditSpanStyle.Mono), new AuditSpan(", api.example")], egress.Route);
    }

    [Fact]
    public void EscapedTextCannotStyleItselfOrPoseAsALink()
    {
        var untrusted = "run `rm -rf ~` and see [[request 9]] \\ done";

        var spans = AuditText.Parse("said: " + AuditText.Escape(untrusted) + " · `mono`");

        Assert.Equal([new AuditSpan("said: " + untrusted + " · "), new AuditSpan("mono", AuditSpanStyle.Mono)], spans);
    }

    [Fact]
    public async Task TypingInTheFilterChangesOnlyTheRowsThatComeAndGo()
    {
        var audit = await Open();
        var before = audit.Rows.ToList();
        var changes = new List<NotifyCollectionChangedAction>();
        audit.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);

        audit.PathFilter = "w";
        audit.PathFilter = "we";
        audit.PathFilter = "web";

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.All(audit.Rows, row => Assert.Contains(row, before));
        Assert.Equal([1191L, 1189, 1177], audit.Rows.Select(row => row.Sequence));

        // Reset puts the rows back in one pass: each comes back once, in the log's order.
        changes.Clear();
        audit.Filters.Single(f => f.Group == AuditEventGroup.Checkpoints).IsChecked = true;
        changes.Clear();
        audit.ResetCommand.Execute(null);
        Assert.Equal(before.Select(row => row.Sequence), audit.Rows.Select(row => row.Sequence));
        Assert.Equal(before.Count - 3, changes.Count(action => action == NotifyCollectionChangedAction.Add));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, changes);
    }

    [Fact]
    public async Task WhatLeftTheMachineLandsOnTheRequestWhateverTheFiltersWere()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        var audit = await Open(shell: shell);
        audit.Filters.Single(f => f.Group == AuditEventGroup.ModelSwitches).IsChecked = false;
        audit.PathFilter = "printenv";
        await audit.ExportCommand.ExecuteAsync();
        Assert.Null(audit.Egress);

        shell.Follow(new AreaLink(Area.Audit, "cli-9f2e", AreaLinkTarget.Egress));
        await Task.Yield();

        Assert.Equal(1194, audit.SelectedRow?.Sequence);
        Assert.Equal(7, audit.Egress?.Request.Number);
        Assert.Equal("", audit.PathFilter);
        Assert.True(audit.Filters.Single(f => f.Group == AuditEventGroup.ModelSwitches).IsChecked);
        Assert.False(audit.HasExportResult);
    }

    [Fact]
    public async Task OpenInAuditFromTheSessionsDockStartsFromTheUsualFilters()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        var audit = await Open(shell: shell);
        audit.Detection!.Events[0].Open.Execute(null);
        audit.PathFilter = "shell";
        Assert.True(audit.HasMatch);

        shell.Follow(new AreaLink(Area.Audit, "cli-9f2e"));
        await Task.Yield();

        Assert.False(audit.HasMatch);
        Assert.Equal("", audit.PathFilter);
        Assert.Equal("newest first · any transcript row jumps here", audit.EventsNote);
        Assert.Equal(14, audit.Rows.Count);
    }

    [Fact]
    public async Task AStaleLoadsFailureDoesNotOverwriteTheNewerLog()
    {
        var time = ManualTimeProvider.Demo();
        var service = new SlowAuditService(new DemoAuditService(time));
        var audit = await Open(service);

        service.Slow = DemoAuditService.ReleaseNotesSessionId;
        audit.SessionIndex = 1;
        service.Slow = null;
        audit.SessionIndex = 2;
        Assert.Equal("cli-77b0", audit.SessionId);
        service.Release.SetException(new IOException("the old pick failed"));
        await Task.Yield();

        Assert.Equal("", audit.Error);
        Assert.All(audit.Rows, row => Assert.Equal("cli-77b0", row.Event.SessionId));
    }

    [Fact]
    public async Task MoveSelectionUpFromNothingTakesTheLastRow()
    {
        // As documented: with nothing selected, Down takes the first row and Up the last.
        var audit = await Open();
        audit.ClearSelectionCommand.Execute(null);
        audit.MoveSelection(-1);
        Assert.Equal(audit.Rows[^1], audit.SelectedRow);
        audit.ClearSelectionCommand.Execute(null);
        audit.MoveSelection(1);
        Assert.Equal(audit.Rows[0], audit.SelectedRow);
    }

    [Fact]
    public async Task TheTracesFileNameTakesTheSessionIdAsData()
    {
        var service = new DemoAuditService(ManualTimeProvider.Demo());

        var result = await service.ExportAsync("../../.ssh/id", new TraceExportOptions(TraceRetention.MetadataOnly, false), TestContext.Current.CancellationToken);

        Assert.Equal("~/.local/share/corvid/traces/.._.._.ssh_id-20261024-1405.jsonl", result.Path);
        Assert.Equal("session", DemoAuditService.FileNamePart(".."));
    }

    /// <summary>The demo, with one session's read held back until the test releases it (with a failure).</summary>
    private sealed class SlowAuditService(DemoAuditService inner) : IAuditService
    {
        public string? Slow { get; set; }

        public TaskCompletionSource<IReadOnlyList<AuditEvent>> Release { get; } = new();

        public Task<AuditLogInfo> GetLogAsync(CancellationToken cancellationToken) => inner.GetLogAsync(cancellationToken);

        public Task<IReadOnlyList<AuditSession>> ListSessionsAsync(CancellationToken cancellationToken) => inner.ListSessionsAsync(cancellationToken);

        public Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(string? sessionId, CancellationToken cancellationToken) =>
            sessionId is not null && sessionId == Slow ? Release.Task : inner.ReadEventsAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<EgressRequest>> ListEgressAsync(string? sessionId, CancellationToken cancellationToken) => inner.ListEgressAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<DetectionMatch>> ListDetectionsAsync(string? sessionId, CancellationToken cancellationToken) =>
            inner.ListDetectionsAsync(sessionId, cancellationToken);

        public Task<ScanResult?> GetLastScanAsync(string? sessionId, CancellationToken cancellationToken) => inner.GetLastScanAsync(sessionId, cancellationToken);

        public Task<ScanResult> ScanAsync(string? sessionId, string failOn, CancellationToken cancellationToken) => inner.ScanAsync(sessionId, failOn, cancellationToken);

        public Task<TraceExportResult> ExportAsync(string? sessionId, TraceExportOptions options, CancellationToken cancellationToken) =>
            inner.ExportAsync(sessionId, options, cancellationToken);
    }
}
