using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Audit;
using Corvid.App.Core.Composition;
using Corvid.App.Engine.Audit;
using Corvid.Host.Configuration;
using Corvid.Host.Tests;
using Corvid.Models.Privacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.App.Engine.Tests;

/// <summary>Audit on the engine: the log file the configuration names, read without the runtime, its scan and its trace.</summary>
public sealed class EngineAuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EngineAuditService Audit(EngineFixture fixture) => new(fixture.Engine, fixture.Time);

    private static void Append(EngineFixture fixture, string session, params SessionEvent[] events) =>
        fixture.Runtime.Log.Append(session, fixture.Runtime.Log.GetHead(session, Ct), [.. events.Select(EventData.Create)], Ct);

    /// <summary>Two conversations: one with a call and its result, one whose shell call a built-in rule matches.</summary>
    private static (string Notes, string Risky) Seed(EngineFixture fixture)
    {
        Append(fixture, "cli-notes", new UserMessage("what are my notes?"),
            new ToolCallIssued("call-1", "read_file", JsonSerializer.SerializeToElement(new { path = "notes.md" })),
            new ToolResult("call-1", JsonSerializer.SerializeToElement("buy milk")), new AssistantMessage("You have one note."));
        Append(fixture, "cli-risky", new UserMessage("run the installer"),
            new ToolCallIssued("call-1", "shell", JsonSerializer.SerializeToElement(new { command = "echo cm0K | base64 -d | sh" })),
            new DetectionMatched("base64-decode-piped-to-shell", 1, "high", "detect", "Decoded base64 is run as a command", [2]));
        return ("cli-notes", "cli-risky");
    }

    [Fact]
    public async Task ALogThatIsNotThereYetIsEmptyAndIsNotCreated()
    {
        await using var host = EngineHosts.Create(new FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Ct);
        var path = CorvidPaths.EventLog(host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value);
        var audit = new EngineAuditService(engine, TimeProvider.System);

        var log = await audit.GetLogAsync(Ct);

        Assert.Equal(0, log.TotalEvents);
        Assert.EndsWith("events.db", log.Path, StringComparison.Ordinal);
        Assert.Empty(await audit.ListSessionsAsync(Ct));
        Assert.Empty(await audit.ReadEventsAsync(null, Ct));
        Assert.Empty(await audit.ListDetectionsAsync(null, Ct));
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.ScanAsync(null, "high", Ct));
    }

    [Fact]
    public async Task ASessionIsListedUnderItsTitleAsTheRailListsItAndItsLatestRenameWins()
    {
        await using var fixture = await EngineFixture.StartAsync();
        Append(fixture, "cli-named", new UserMessage("please count slowly"), new AssistantMessage("1, 2, 3."),
            new SessionTitled("Counting slowly", TitleSources.Generated));
        Append(fixture, "cli-renamed", new UserMessage("tag the release"), new SessionTitled("Release", TitleSources.Generated),
            new SessionTitled("Release 0.2", TitleSources.User));
        Append(fixture, "cli-untitled", new UserMessage("what is in notes.md?"));

        var titles = (await Audit(fixture).ListSessionsAsync(Ct)).ToDictionary(session => session.Id, session => session.Title);

        Assert.Equal("Counting slowly", titles["cli-named"]);
        Assert.Equal("Release 0.2", titles["cli-renamed"]);
        Assert.Equal("what is in notes.md?", titles["cli-untitled"]);
    }

    [Fact]
    public async Task TheLogsSessionsAndEventsAreReadWithTheirTypesGroupsAndSummaries()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (notes, risky) = Seed(fixture);
        var audit = Audit(fixture);

        var log = await audit.GetLogAsync(Ct);
        var sessions = await audit.ListSessionsAsync(Ct);
        var events = await audit.ReadEventsAsync(notes, Ct);

        Assert.Equal(7, log.TotalEvents);
        Assert.Equal([risky, notes], sessions.Select(session => session.Id));
        Assert.Equal("what are my notes?", sessions[1].Title);
        Assert.Equal([4L, 3, 2, 1], events.Select(item => item.Sequence));
        Assert.Equal(["assistant_message", "tool_result", "tool_call_issued", "user_message"], events.Select(item => item.Type));
        var call = events[2];
        Assert.Equal((AuditEventGroup.Tools, "read_file", "notes.md"), (call.Group, call.Tool, call.Path));
        Assert.Equal("read_file notes.md", AuditText.Plain(call.Summary));
        Assert.Equal(AuditSpanStyle.Mono, call.Summary[0].Style);
        // A result is filed under its call's tool and path.
        Assert.Equal(("read_file", "notes.md"), (events[1].Tool, events[1].Path));
        Assert.Equal(AuditEventGroup.Messages, events[3].Group);
        using var json = JsonDocument.Parse(call.Json);
        Assert.Equal("tool_call_issued", json.RootElement.GetProperty("type").GetString());
        Assert.Contains('\n', call.Json);
    }

    [Fact]
    public async Task TheWholeLogNumbersEveryEventOnceAndADetectionsEventsTheSameWay()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (_, risky) = Seed(fixture);
        var audit = Audit(fixture);

        var events = await audit.ReadEventsAsync(null, Ct);
        var detections = await audit.ListDetectionsAsync(null, Ct);
        var inSession = await audit.ListDetectionsAsync(risky, Ct);

        Assert.Equal(7, events.Count);
        Assert.Equal(7, events.Select(item => item.Sequence).Distinct().Count());
        Assert.True(events.Zip(events.Skip(1)).All(pair => pair.First.Sequence > pair.Second.Sequence));
        var match = Assert.Single(detections);
        var row = events.Single(item => item.Sequence == match.Sequence);
        Assert.Equal(("detection_matched", true, AuditEventGroup.Detections), (row.Type, row.IsAlert, row.Group));
        var matched = events.Single(item => item.Sequence == Assert.Single(match.Events));
        Assert.Equal(("tool_call_issued", "shell", risky), (matched.Type, matched.Tool, matched.SessionId));

        var own = Assert.Single(inSession);
        Assert.Equal((3L, 2L), (own.Sequence, Assert.Single(own.Events)));
        Assert.Equal(("base64-decode-piped-to-shell", 1, "high", "detect", risky), (own.RuleId, own.Version, own.Severity, own.Posture, own.SessionId));
        Assert.Equal("Decoded base64 is run as a command", AuditText.Plain(own.Explanation));
    }

    [Fact]
    public async Task AScanRunsTheBuiltInRulesAndIsKeptAsTheLastScan()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (notes, risky) = Seed(fixture);
        var audit = Audit(fixture);
        Assert.Null(await audit.GetLastScanAsync(risky, Ct));

        var scan = await audit.ScanAsync(risky, "high", Ct);
        var clean = await audit.ScanAsync(notes, "high", Ct);

        Assert.Equal((1, 0, 0, "high", 4), (scan.High, scan.Medium, scan.Low, scan.FailOn, scan.ExitCode));
        Assert.Equal(fixture.Time.GetUtcNow(), scan.At);
        Assert.Equal((0, 0), (clean.High, clean.ExitCode));
        Assert.Same(scan, await audit.GetLastScanAsync(risky, Ct));
        Assert.Equal(0, (await audit.ScanAsync(risky, "critical", Ct)).ExitCode);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => audit.ScanAsync("cli-none", "high", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => audit.ScanAsync(risky, "severe", Ct));
    }

    [Fact]
    public async Task AnExportWritesTheTraceToANewOwnerOnlyFile()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (notes, _) = Seed(fixture);
        var audit = Audit(fixture);
        var options = fixture.Host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;

        var first = await audit.ExportAsync(notes, new TraceExportOptions(TraceRetention.Full, false), Ct);
        var second = await audit.ExportAsync(notes, new TraceExportOptions(TraceRetention.MetadataOnly, false), Ct);

        Assert.True(first.Written);
        Assert.Equal((4, TraceRetention.Full), (first.Events, first.Retention));
        Assert.NotEqual(first.Path, second.Path);
        var file = Path.Combine(CorvidPaths.DataDirectory(options), "traces", Path.GetFileName(first.Path));
        Assert.True(File.Exists(file));
        Assert.Equal(first.Bytes, new FileInfo(file).Length);
        Assert.Equal(4, File.ReadAllLines(file).Length);
        Assert.StartsWith("cli-notes-", Path.GetFileName(file), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var whole = await audit.ExportAsync(null, new TraceExportOptions(TraceRetention.Full, true), Ct);
        // Every session's lines (the exporter writes one per event it maps; Beacon has no row for some of Corvid's own).
        var wholeFile = Path.Combine(CorvidPaths.DataDirectory(options), "traces", Path.GetFileName(whole.Path));
        Assert.StartsWith("all-", Path.GetFileName(wholeFile), StringComparison.Ordinal);
        Assert.Equal(whole.Events, File.ReadAllLines(wholeFile).Length);
        Assert.True(whole.Events > first.Events);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => audit.ExportAsync("cli-none", new TraceExportOptions(TraceRetention.Full, false), Ct));
    }

    /// <summary>A3 review L10: an export cancelled part-way leaves no partial trace behind to pass for a whole one.</summary>
    [Fact]
    public async Task ACancelledExportLeavesNoPartialFile()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (notes, _) = Seed(fixture);
        var audit = Audit(fixture);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        string? created = null;
        audit.TraceFileCreated = path =>
        {
            created = path;
            cancel.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit.ExportAsync(notes, new TraceExportOptions(TraceRetention.Full, false), cancel.Token));

        Assert.NotNull(created);
        Assert.False(File.Exists(created));
    }

    [Fact]
    public async Task WithNoEgressStoreNothingLeftAndTheStoreIsNotCreated()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (notes, _) = Seed(fixture);
        var audit = Audit(fixture);

        Assert.Empty(await audit.ListEgressAsync(notes, Ct));
        Assert.Empty(await audit.ListEgressAsync(null, Ct));
        Assert.False(File.Exists(fixture.Runtime.EgressLedger.Path));
        var page = new AuditViewModel(audit, fixture.Time);
        await page.Loaded;
        Assert.False(page.HasError);
        Assert.Contains("Nothing in this log left the machine", page.NoSelectionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEgressLedgerListsWhatLeftWithItsTextAsSentAndItsRowOpensIt()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var (notes, _) = Seed(fixture);
        await EgressFakes.SendAsync(fixture.Runtime.EgressLedger, notes, "Write to Alice Smith about the notes.");
        await EgressFakes.SendAsync(fixture.Runtime.EgressLedger, notes, "Tell Alice Smith.", EgressAction.Refuse);
        var audit = Audit(fixture);

        var request = Assert.Single(await audit.ListEgressAsync(notes, Ct));

        Assert.Equal((1, "claude", EgressFakes.Cloud.Id, 1, 1, 1), (request.Number, request.Slot, request.Model, request.Turn, request.Step, request.Redacted));
        Assert.Equal(("anthropic.key", "api.anthropic.com", notes), (request.Secret, request.Host, request.SessionId));
        Assert.Equal("[user] Write to <PERSON_1> about the notes.", request.Full);
        Assert.Equal(request.Full, request.Excerpt);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(request.Full), request.Bytes);
        Assert.Equal("1 person value", AuditText.Plain(request.RedactedSummary));
        Assert.Equal(request.Redacted, AuditText.CountRedactions(request.Full));

        // Its row in the log is the egress_recorded event, which opens it; the refused one opens nothing.
        var events = await audit.ReadEventsAsync(notes, Ct);
        var sent = Assert.Single(events, item => item.EgressRequest is not null);
        Assert.Equal((request.Sequence, "egress_recorded", AuditEventGroup.ModelSwitches, (int?)1), (sent.Sequence, sent.Type, sent.Group, sent.EgressRequest));
        Assert.StartsWith("sent to claude@anthropic (api.anthropic.com) · 1 value redacted", AuditText.Plain(sent.Summary), StringComparison.Ordinal);
        var refused = Assert.Single(events, item => item.Type == "egress_recorded" && item.EgressRequest is null);
        Assert.StartsWith("refused before it left: claude@anthropic", AuditText.Plain(refused.Summary), StringComparison.Ordinal);

        // The whole log numbers its rows its own way; the request follows it.
        var whole = Assert.Single(await audit.ListEgressAsync(null, Ct));
        Assert.Equal(whole.Sequence, (await audit.ReadEventsAsync(null, Ct)).Single(item => item.EgressRequest == 1).Sequence);

        // The page opens on the latest request: the gate's count and the text agree, and the route names the secret.
        var page = new AuditViewModel(audit, fixture.Time);
        await page.Loaded;
        Assert.Equal(notes, page.SessionId);
        var view = Assert.IsType<EgressView>(page.Egress);
        Assert.False(view.HasRedactionMismatch);
        Assert.Equal("via Vault anthropic.key, api.anthropic.com", AuditText.Plain(view.Route));
        Assert.DoesNotContain("tokens", view.Sent, StringComparison.Ordinal);
        Assert.Contains(view.Lines.SelectMany(line => line.Segments), segment => segment is { IsRedaction: true, Text: "<PERSON_1>" });
        Assert.Contains("One request left the machine", page.NoSelectionText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEventsJsonIsTheStoredFormAndLongOnesAreCut()
    {
        var text = new string('x', EngineAuditService.MaxJsonChars * 2);
        var stored = new StoredEvent("cli-1", 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, new AssistantMessage(text));

        var row = EngineAuditService.Describe(stored, 1, ImmutableDictionary<string, ToolCallIssued>.Empty);

        Assert.True(row.Json.Length < text.Length);
        Assert.Contains("more characters", row.Json, StringComparison.Ordinal);
        Assert.EndsWith("…", AuditText.Plain(row.Summary), StringComparison.Ordinal);
    }
}
