using System.Text.Json;
using Corvid.App.Core.Audit;

namespace Corvid.App.Core.Tests;

public sealed class DemoAuditServiceTests
{
    private static DemoAuditService Service() => new(ManualTimeProvider.Demo());

    [Fact]
    public async Task TheMainSessionsLogHoldsAuditPngsRowsNewestFirst()
    {
        var service = Service();
        var events = await service.ReadEventsAsync(DemoAuditService.MainSessionId, TestContext.Current.CancellationToken);

        var shown = new long[] { 1203, 1202, 1201, 1196, 1194, 1191, 1190, 1189, 1182, 1180, 1177, 1170, 1168 };
        Assert.Equal(shown, events.Select(e => e.Sequence).Where(shown.Contains));
        Assert.True(events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderDescending()));
        Assert.All(events, e => Assert.Equal(DemoAuditService.MainSessionId, e.SessionId));

        var detection = events.Single(e => e.Sequence == 1190);
        Assert.True(detection.IsAlert);
        Assert.Equal(("detection_matched", AuditEventGroup.Detections), (detection.Type, detection.Group));
        Assert.Equal("credential-read-then-egress v3 · high · enforce → forced an ask · events 1182, 1189", AuditText.Plain(detection.Summary));

        var switched = events.Single(e => e.Sequence == 1194);
        Assert.Equal(7, switched.EgressRequest);
        Assert.Equal(new AuditSpan("request 7 in the egress ledger", AuditSpanStyle.Link), switched.Summary[^1]);
        Assert.Equal("14:03:52", AuditText.Time(switched.At, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task EveryRawEventIsJsonWithItsSequenceAndType()
    {
        var events = await Service().ReadEventsAsync(null, TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e.SessionId == DemoAuditService.ReleaseNotesSessionId);
        foreach (var auditEvent in events)
        {
            using var json = JsonDocument.Parse(auditEvent.Json);
            Assert.Equal(auditEvent.Sequence, json.RootElement.GetProperty("seq").GetInt64());
            Assert.Equal(auditEvent.Type, json.RootElement.GetProperty("type").GetString());
        }

        Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
        Assert.All(events, e => Assert.True(e.Sequence <= DemoAuditService.TotalEvents));
    }

    [Fact]
    public async Task RequestSevenIsTheEscalationWithTwoValuesRedacted()
    {
        var service = Service();
        var egress = Assert.Single(await service.ListEgressAsync(DemoAuditService.MainSessionId, TestContext.Current.CancellationToken));

        Assert.Equal((7, 1194L, "claude-opus-5-5", 2), (egress.Number, egress.Sequence, egress.Model, egress.Redacted));
        Assert.Contains("GH_TOKEN=[redacted:credential]", egress.Excerpt, StringComparison.Ordinal);
        Assert.Contains("GIT_AUTHOR_EMAIL=[redacted:email]", egress.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", egress.Full, StringComparison.Ordinal);
        Assert.Empty(await service.ListEgressAsync(DemoAuditService.VaultSessionId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AScanStampsTheClockAndFailsOnTheHighMatch()
    {
        var service = Service();
        var seeded = await service.GetLastScanAsync(DemoAuditService.MainSessionId, TestContext.Current.CancellationToken);
        Assert.NotNull(seeded);

        var scan = await service.ScanAsync(DemoAuditService.MainSessionId, "high", TestContext.Current.CancellationToken);

        Assert.Equal("14:05 · 1 high, 0 medium · would exit 4 with --fail-on high", AuditText.Scan(scan, TimeZoneInfo.Utc));
        Assert.Same(scan, await service.GetLastScanAsync(DemoAuditService.MainSessionId, TestContext.Current.CancellationToken));
        var clean = await service.ScanAsync(DemoAuditService.VaultSessionId, "high", TestContext.Current.CancellationToken);
        Assert.Equal(0, clean.ExitCode);
    }

    [Fact]
    public async Task MetadataRetentionDropsTheContentAndMemoryStaysOutUnlessAsked()
    {
        var service = Service();
        var ct = TestContext.Current.CancellationToken;
        var full = await service.ExportAsync(DemoAuditService.MainSessionId, new TraceExportOptions(TraceRetention.Full, false), ct);
        var metadata = await service.ExportAsync(DemoAuditService.MainSessionId, new TraceExportOptions(TraceRetention.MetadataOnly, false), ct);
        var withMemory = await service.ExportAsync(DemoAuditService.MainSessionId, new TraceExportOptions(TraceRetention.MetadataOnly, true), ct);

        Assert.Equal(full.Events, metadata.Events);
        Assert.True(metadata.Bytes < full.Bytes);
        Assert.Equal(metadata.Events + 1, withMemory.Events);
        Assert.EndsWith(".jsonl", metadata.Path, StringComparison.Ordinal);

        var events = await service.ReadEventsAsync(DemoAuditService.MainSessionId, ct);
        var line = DemoAuditService.TraceLine(events.Single(e => e.Sequence == 1182), TraceRetention.MetadataOnly);
        Assert.DoesNotContain("summary", line, StringComparison.Ordinal);
        Assert.Contains("\"tool\":\"shell\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkupSplitsMonoAndLinkSpans()
    {
        var spans = AuditText.Parse("a-32 · shell `rm -rf build/` · [[request 7]] end");

        Assert.Equal(
        [
            new AuditSpan("a-32 · shell "), new AuditSpan("rm -rf build/", AuditSpanStyle.Mono), new AuditSpan(" · "),
            new AuditSpan("request 7", AuditSpanStyle.Link), new AuditSpan(" end"),
        ], spans);
        Assert.Equal([new AuditSpan("an `unclosed tick")], AuditText.Parse("an `unclosed tick"));
    }

    [Fact]
    public void RedactionMarkersAreCutOutOfALine()
    {
        Assert.Equal(
            [new RedactedSegment("GH_TOKEN=", false), new RedactedSegment("[redacted:credential]", true), new RedactedSegment(" x", false)],
            AuditText.Redactions("GH_TOKEN=[redacted:credential] x"));
        Assert.Equal([new RedactedSegment("CI=true", false)], AuditText.Redactions("CI=true"));
        Assert.Equal(["38.2 KB", "812 B", "1,214"], [AuditText.Size(38_200), AuditText.Size(812), AuditText.Count(1214)]);
    }
}
