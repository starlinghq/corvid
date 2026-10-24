using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Corvid.App.Core.Audit;

/// <summary>
/// The Audit area's demo: the event log at Audit.png's moment (2026-10-24 14:05, turn 12 of <c>cli-9f2e</c> waiting on
/// approval a-32), with the escalation to claude as request 7 of the egress ledger, the credential-read-then-egress
/// match, and the other demo sessions' events interleaved in the shared log. Times are wall-clock times on the scenario's
/// day in the clock's zone, as the Sessions demo writes them. Nothing touches the disk: a scan answers from the seeded
/// matches and an export builds its lines in memory and reports them.
/// <para>
/// The Audit mockup was drawn on its own, so its call numbers (call-4 the fetch, call-5 <c>printenv</c>, a-31 denied)
/// are its own rather than the Sessions demo's turn 12; the overlap (ids, titles, a-32, the two redacted values, the
/// docs.example.org taint, the 1,214 events) is shared.
/// </para>
/// </summary>
public sealed class DemoAuditService : IAuditService
{
    public const string MainSessionId = "cli-9f2e";
    public const string ReleaseNotesSessionId = "cli-a410";
    public const string VaultSessionId = "cli-77b0";
    public const string TerminalSessionId = "cli-c3d2";

    /// <summary>The whole log's size at the demo's moment.</summary>
    public const long TotalEvents = 1214;

    public const string LogPath = "~/.local/share/corvid/events.db";

    private const string CredentialRule = "credential-read-then-egress";

    private readonly TimeProvider _time;
    private readonly List<AuditEvent> _events = [];
    private readonly List<EgressRequest> _egress = [];
    private readonly List<DetectionMatch> _detections = [];
    private readonly Dictionary<string, ScanResult> _scans = new(StringComparer.Ordinal);

    public DemoAuditService(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        Seed();
    }

    public static IReadOnlyList<AuditSession> Sessions { get; } =
    [
        new(MainSessionId, "Fix the flaky sandbox test on CI"),
        new(ReleaseNotesSessionId, "Draft release notes for v0.2"),
        new(VaultSessionId, "Vault policy for ntfy"),
        new(TerminalSessionId, "Spike S4 write-up"),
    ];

    public Task<AuditLogInfo> GetLogAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AuditLogInfo(TotalEvents, LogPath));

    public Task<IReadOnlyList<AuditSession>> ListSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(Sessions);

    public Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(string? sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEvent>>([.. Of(_events, sessionId, e => e.SessionId).OrderByDescending(e => e.Sequence)]);

    public Task<IReadOnlyList<EgressRequest>> ListEgressAsync(string? sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EgressRequest>>([.. Of(_egress, sessionId, e => e.SessionId).OrderByDescending(e => e.Number)]);

    public Task<IReadOnlyList<DetectionMatch>> ListDetectionsAsync(string? sessionId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DetectionMatch>>([.. Of(_detections, sessionId, d => d.SessionId).OrderByDescending(d => d.Sequence)]);

    public Task<ScanResult?> GetLastScanAsync(string? sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_scans.GetValueOrDefault(sessionId ?? ""));

    public Task<ScanResult> ScanAsync(string? sessionId, string failOn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failOn);
        cancellationToken.ThrowIfCancellationRequested();
        var matches = Of(_detections, sessionId, d => d.SessionId).ToList();
        int Count(string severity) => matches.Count(d => string.Equals(d.Severity, severity, StringComparison.Ordinal));
        var (high, medium, low) = (Count("high"), Count("medium"), Count("low"));
        // corvid audit scan exits 4 when a match is at or above the --fail-on severity.
        var failing = failOn switch
        {
            "low" => high + medium + low,
            "medium" => high + medium,
            _ => high,
        };
        var scan = new ScanResult(_time.GetUtcNow(), high, medium, low, failOn, failing > 0 ? 4 : 0);
        _scans[sessionId ?? ""] = scan;
        return Task.FromResult(scan);
    }

    public Task<TraceExportResult> ExportAsync(string? sessionId, TraceExportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var events = Of(_events, sessionId, e => e.SessionId)
            .Where(e => options.IncludeLocalMemory || e.Group != AuditEventGroup.MemoryAndSkills)
            .OrderBy(e => e.Sequence)
            .ToList();
        var bytes = events.Sum(e => (long)Encoding.UTF8.GetByteCount(TraceLine(e, options.Retention)) + 1);
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var path = $"~/.local/share/corvid/traces/{sessionId ?? "all"}-{stamp}.jsonl";
        // The demo touches nothing on the person's machine: it reports the trace without writing it.
        return Task.FromResult(new TraceExportResult(path, events.Count, bytes, options.Retention, Written: false));
    }

    /// <summary>
    /// One Beacon JSONL line. Metadata retention keeps the event's type, time, tool and path and drops the summary's
    /// content; full retention keeps the summary, cut at 4 KiB.
    /// </summary>
    public static string TraceLine(AuditEvent auditEvent, TraceRetention retention)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", auditEvent.Sequence);
            writer.WriteString("session", auditEvent.SessionId);
            writer.WriteString("at", auditEvent.At);
            writer.WriteString("type", auditEvent.Type);
            if (auditEvent.Tool is { } tool)
                writer.WriteString("tool", tool);
            if (auditEvent.Path is { } path)
                writer.WriteString("path", path);
            if (retention == TraceRetention.Full)
            {
                var summary = AuditText.Plain(auditEvent.Summary);
                writer.WriteString("summary", summary.Length > 4096 ? summary[..4096] : summary);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IEnumerable<T> Of<T>(IEnumerable<T> items, string? sessionId, Func<T, string> session) =>
        sessionId is null ? items : items.Where(item => string.Equals(session(item), sessionId, StringComparison.Ordinal));

    /// <summary>A wall-clock time on the scenario's day, in the clock's zone.</summary>
    private DateTimeOffset At(int hour, int minute, int second = 0)
    {
        var zone = _time.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
        var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private void Add(long sequence, string session, DateTimeOffset at, string type, AuditEventGroup group, string summary,
        string? tool = null, string? path = null, int? egress = null, bool alert = false, params (string Key, object Value)[] fields)
    {
        var spans = AuditText.Parse(summary);
        _events.Add(new AuditEvent(sequence, session, at, type, group)
        {
            Summary = spans,
            Tool = tool,
            Path = path,
            EgressRequest = egress,
            IsAlert = alert,
            Json = Json(sequence, session, at, type, tool, path, fields),
        });
    }

    private static string Json(long sequence, string session, DateTimeOffset at, string type, string? tool, string? path,
        (string Key, object Value)[] fields)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", sequence);
            writer.WriteString("session", session);
            writer.WriteString("at", at);
            writer.WriteString("type", type);
            if (tool is not null)
                writer.WriteString("tool", tool);
            if (path is not null)
                writer.WriteString("path", path);
            foreach (var (key, value) in fields)
            {
                switch (value)
                {
                    case int number:
                        writer.WriteNumber(key, number);
                        break;
                    case long number:
                        writer.WriteNumber(key, number);
                        break;
                    case double number:
                        writer.WriteNumber(key, number);
                        break;
                    case bool flag:
                        writer.WriteBoolean(key, flag);
                        break;
                    case string[] list:
                        writer.WriteStartArray(key);
                        foreach (var item in list)
                            writer.WriteStringValue(item);
                        writer.WriteEndArray();
                        break;
                    case long[] list:
                        writer.WriteStartArray(key);
                        foreach (var item in list)
                            writer.WriteNumberValue(item);
                        writer.WriteEndArray();
                        break;
                    default:
                        writer.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void Seed()
    {
        SeedVaultSession();
        SeedMainSession();
        SeedReleaseNotes();
        SeedTerminalSession();
        _events.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
    }

    // ---- cli-9f2e: Audit.png's rows, and the hidden groups' events between them ----

    private void SeedMainSession()
    {
        const string S = MainSessionId;
        const AuditEventGroup Messages = AuditEventGroup.Messages, Tools = AuditEventGroup.Tools, Approvals = AuditEventGroup.Approvals;
        Add(1166, S, At(13, 59, 2), "assistant_message", Messages, "turn 11 · the seccomp test patched and passing locally · 412 tokens",
            fields: [("turn", 11), ("tokens", 412)]);
        Add(1167, S, At(13, 59, 3), "checkpoint_recorded", AuditEventGroup.Checkpoints, "c-43 · after call-d2 · `patch_file` tests/…/SeccompTests.cs",
            tool: "patch_file", path: "tests/Corvid.Sandbox.Tests/SeccompTests.cs", fields: [("checkpoint", "c-43"), ("kind", "after")]);
        Add(1168, S, At(14, 1, 5), "user_message", Messages, "the sandbox test fails on CI only…",
            fields: [("turn", 12), ("text", "the sandbox test fails on CI only. find out why and fix it without touching the production launcher.")]);
        Add(1169, S, At(14, 2, 38), "compaction_chunk", AuditEventGroup.Compaction, "chunks 1 and 2 · events 912 to 1104 · qwen3.5-0.8b",
            fields: [("from", 912), ("to", 1104), ("chunks", 2), ("summarizer", "qwen3.5-0.8b")]);
        Add(1170, S, At(14, 2, 40), "compacted", Messages,
            "events 912 to 1104 · goal kept · 3 decisions · 2 open questions · summarizer qwen3.5-0.8b · 2 chunks",
            fields: [("from", 912), ("to", 1104), ("goal_kept", true), ("decisions", 3), ("open_questions", 2), ("summarizer", "qwen3.5-0.8b"), ("chunks", 2)]);
        Add(1174, S, At(14, 3, 9), "memory_recalled", AuditEventGroup.MemoryAndSkills, "2 facts · 1 lesson · for turn 12 step 1",
            fields: [("facts", 2), ("lessons", 1), ("turn", 12)]);
        Add(1177, S, At(14, 3, 12), "tool_result", Tools, "call-4 · web_fetch · docs.example.org · 48 KB · taints the session",
            tool: "web_fetch", path: "https://docs.example.org/bubblewrap/seccomp",
            fields: [("call", "call-4"), ("outcome", "ok"), ("bytes", 48210), ("taint", "web_fetch docs.example.org")]);
        Add(1180, S, At(14, 3, 30), "tool_call_issued", Tools, "call-5 · shell · `printenv` · approved once",
            tool: "shell", path: "printenv", fields: [("call", "call-5"), ("command", "printenv"), ("approval", "once")]);
        Add(1181, S, At(14, 3, 30), "checkpoint_recorded", AuditEventGroup.Checkpoints, "c-44 · before call-5 · nothing to restore (read-only)",
            tool: "shell", fields: [("checkpoint", "c-44"), ("kind", "before"), ("call", "call-5")]);
        Add(1182, S, At(14, 3, 31), "tool_result", Tools, "call-5 · shell `printenv` · ok · scrubbed: 1 value (`[secret:github.token]`)",
            tool: "shell", path: "printenv",
            fields: [("call", "call-5"), ("outcome", "ok"), ("scrubbed", 1), ("secrets", new[] { "github.token" }), ("credential_read", true)]);
        Add(1189, S, At(14, 3, 49), "tool_call_issued", Tools, "call-7 · web_fetch · POST https://hooks.example.org/ci · tainted",
            tool: "web_fetch", path: "https://hooks.example.org/ci",
            fields: [("call", "call-7"), ("method", "POST"), ("url", "https://hooks.example.org/ci"), ("tainted", true), ("egress", true)]);
        Add(1190, S, At(14, 3, 49), "detection_matched", AuditEventGroup.Detections,
            "`credential-read-then-egress` v3 · high · enforce → forced an ask · events 1182, 1189", alert: true,
            fields: [("rule", CredentialRule), ("version", 3), ("severity", "high"), ("posture", "enforce"), ("events", new long[] { 1182, 1189 }), ("forced_ask", "a-31")]);
        Add(1191, S, At(14, 3, 50), "approval_resolved", Approvals, "a-31 · denied by you · \"not from a tainted session\"",
            tool: "web_fetch", path: "https://hooks.example.org/ci",
            fields: [("approval", "a-31"), ("call", "call-7"), ("outcome", "denied"), ("reason", "not from a tainted session")]);
        Add(1194, S, At(14, 3, 52), "model_switched", AuditEventGroup.ModelSwitches,
            "generator · qwen3.5-0.8b → claude · step budget · [[request 7 in the egress ledger]]", egress: 7,
            fields: [("slot", "generator"), ("from", "qwen3.5-0.8b"), ("to", "claude"), ("reason", "step budget (difficulty 0.71)"), ("egress_request", 7), ("redacted", 2)]);
        Add(1195, S, At(14, 3, 55), "checkpoint_recorded", AuditEventGroup.Checkpoints, "c-45 · before call-8 · `patch_file` tests/…/SeccompTests.cs",
            tool: "patch_file", path: "tests/Corvid.Sandbox.Tests/SeccompTests.cs", fields: [("checkpoint", "c-45"), ("kind", "before"), ("call", "call-8")]);
        Add(1196, S, At(14, 3, 58), "tool_result", Tools, "call-8 · patch_file · ok · scrubbed: 0",
            tool: "patch_file", path: "tests/Corvid.Sandbox.Tests/SeccompTests.cs",
            fields: [("call", "call-8"), ("outcome", "ok"), ("scrubbed", 0), ("added", 9), ("removed", 4)]);
        Add(1201, S, At(14, 4, 11), "tool_call_issued", Tools, "call-9 · shell · round 2",
            tool: "shell", path: "rm -rf build/ && dotnet test", fields: [("call", "call-9"), ("command", "rm -rf build/ && dotnet test"), ("round", 2)]);
        Add(1202, S, At(14, 4, 12), "decision", AuditEventGroup.Decisions, "pre-execute · risk → 3, ask (0.86) · Kev-0.8B · catalogue 2026-10-21",
            fields: [("checkpoint", "pre_execute"), ("question", "risk"), ("answer", "3"), ("action", "ask"), ("confidence", 0.86), ("decider", "Kev-0.8B"), ("catalogue", "2026-10-21")]);
        Add(1203, S, At(14, 4, 12), "approval_requested", Approvals, "a-32 · shell `rm -rf build/ && dotnet test` · risk 3",
            tool: "shell", path: "rm -rf build/ && dotnet test",
            fields: [("approval", "a-32"), ("call", "call-9"), ("command", "rm -rf build/ && dotnet test"), ("risk", 3), ("offered", new[] { "once", "session", "always" })]);

        _egress.Add(new EgressRequest(7, 1194, S, At(14, 3, 52), "claude-opus-5-5")
        {
            Slot = "claude",
            Turn = 12,
            Step = 3,
            Secret = "anthropic.key",
            Host = "api.anthropic.com",
            Bytes = 38_200,
            Tokens = 9410,
            CachedPrefix = 6120,
            Redacted = 2,
            RedactedSummary = AuditText.Parse("one email address, one credential (the `printenv` result)"),
            Excerpt = string.Join('\n',
                "[tool_result call-5 shell printenv]",
                "HOME=/home/dev",
                "GH_TOKEN=[redacted:credential]",
                "GIT_AUTHOR_EMAIL=[redacted:email]",
                "CI=true",
                "…",
                "[user] the sandbox test fails on CI only. find out why and fix it",
                "without touching the production launcher."),
            Full = string.Join('\n',
                "[system] corvid generator · workspace corvid (main) · approval mode smart",
                "[summary] events 912 to 1104: goal \"fix the flaky sandbox test on CI\"; 3 decisions; 2 open questions",
                "[user] the sandbox test fails on CI only. find out why and fix it",
                "without touching the production launcher.",
                "[tool_result call-1 read_file .github/workflows/ci.yml]",
                "name: ci",
                "on: [push, pull_request]",
                "jobs:",
                "  test:",
                "    runs-on: [self-hosted, arch]",
                "[tool_result call-4 web_fetch docs.example.org] (outside content: tainted)",
                "Seccomp and namespaces in bubblewrap …",
                "[tool_result call-5 shell printenv]",
                "HOME=/home/dev",
                "GH_TOKEN=[redacted:credential]",
                "GIT_AUTHOR_EMAIL=[redacted:email]",
                "CI=true",
                "PATH=/usr/local/bin:/usr/bin",
                "[step 3] difficulty 0.71: escalated for the step budget"),
        });

        _detections.Add(new DetectionMatch(CredentialRule, 3, "high", "enforce", 1190, [1182, 1189])
        {
            SessionId = S,
            Enforced = true,
            Explanation = AuditText.Parse(
                "A credential value was read (event 1182, `printenv`) and the next network call would have sent data out "
                + "(event 1189, POST to hooks.example.org). The rule forced an ask; you denied it."),
        });

        // Seeded before the demo's moment; a scan the person runs stamps the clock's time.
        _scans[S] = new ScanResult(At(14, 4, 30), 1, 0, 0, "high", 4);
    }

    // ---- cli-a410: running alongside, so its events fill the main session's gaps in the shared log ----

    private void SeedReleaseNotes()
    {
        const string S = ReleaseNotesSessionId;
        Add(1171, S, At(14, 2, 50), "user_message", AuditEventGroup.Messages, "draft release notes for v0.2 from the merged pull requests");
        Add(1172, S, At(14, 2, 52), "tool_call_issued", AuditEventGroup.Tools, "call-1 · shell · `gh pr list --state merged --base main`",
            tool: "shell", path: "gh pr list --state merged --base main", fields: [("call", "call-1")]);
        Add(1173, S, At(14, 2, 55), "tool_result", AuditEventGroup.Tools, "call-1 · shell · ok · 41 lines", tool: "shell", fields: [("call", "call-1"), ("outcome", "ok")]);
        Add(1175, S, At(14, 3, 10), "tool_call_issued", AuditEventGroup.Tools, "call-2 · read_file · CHANGELOG.md",
            tool: "read_file", path: "CHANGELOG.md", fields: [("call", "call-2")]);
        Add(1178, S, At(14, 3, 14), "tool_result", AuditEventGroup.Tools, "call-2 · read_file · ok · 6.1 KB",
            tool: "read_file", path: "CHANGELOG.md", fields: [("call", "call-2"), ("outcome", "ok")]);
        Add(1183, S, At(14, 3, 33), "question_asked", AuditEventGroup.Messages, "q-2 · group the sandbox fixes under one heading?", fields: [("question", "q-2")]);
        Add(1197, S, At(14, 4, 0), "tool_call_issued", AuditEventGroup.Tools, "call-3 · write_file · docs/release-notes-0.2.md",
            tool: "write_file", path: "docs/release-notes-0.2.md", fields: [("call", "call-3")]);
        Add(1198, S, At(14, 4, 1), "tool_result", AuditEventGroup.Tools, "call-3 · write_file · ok · +58",
            tool: "write_file", path: "docs/release-notes-0.2.md", fields: [("call", "call-3"), ("outcome", "ok")]);
        Add(1204, S, At(14, 4, 20), "decision", AuditEventGroup.Decisions, "step · clarify → ask (0.62) · Kev-0.8B",
            fields: [("checkpoint", "step"), ("question", "clarify"), ("confidence", 0.62)]);
    }

    // ---- cli-77b0: an hour ago, in another workspace ----

    private void SeedVaultSession()
    {
        const string S = VaultSessionId;
        Add(1052, S, At(13, 4, 10), "user_message", AuditEventGroup.Messages, "write a Vault policy that lets ntfy read only its own token");
        Add(1053, S, At(13, 4, 12), "tool_call_issued", AuditEventGroup.Tools, "call-1 · read_file · policies/ntfy.hcl",
            tool: "read_file", path: "policies/ntfy.hcl", fields: [("call", "call-1")]);
        Add(1054, S, At(13, 4, 12), "tool_result", AuditEventGroup.Tools, "call-1 · read_file · ok · 412 B",
            tool: "read_file", path: "policies/ntfy.hcl", fields: [("call", "call-1"), ("outcome", "ok")]);
        Add(1056, S, At(13, 4, 40), "tool_call_issued", AuditEventGroup.Tools, "call-2 · patch_file · policies/ntfy.hcl",
            tool: "patch_file", path: "policies/ntfy.hcl", fields: [("call", "call-2")]);
        Add(1057, S, At(13, 4, 41), "tool_result", AuditEventGroup.Tools, "call-2 · patch_file · ok · +6 −1",
            tool: "patch_file", path: "policies/ntfy.hcl", fields: [("call", "call-2"), ("outcome", "ok")]);
        Add(1058, S, At(13, 4, 50), "assistant_message", AuditEventGroup.Messages, "turn 1 · the policy now grants read on `secret/ntfy/token` only · 188 tokens",
            fields: [("turn", 1), ("tokens", 188)]);
    }

    // ---- cli-c3d2: the session a terminal holds ----

    private void SeedTerminalSession()
    {
        const string S = TerminalSessionId;
        Add(1205, S, At(14, 4, 25), "user_message", AuditEventGroup.Messages, "summarise spike S4's findings for the design doc");
        Add(1206, S, At(14, 4, 27), "tool_call_issued", AuditEventGroup.Tools, "call-1 · read_file · docs/spikes/s4.md",
            tool: "read_file", path: "docs/spikes/s4.md", fields: [("call", "call-1")]);
        Add(1207, S, At(14, 4, 27), "tool_result", AuditEventGroup.Tools, "call-1 · read_file · ok · 9.8 KB",
            tool: "read_file", path: "docs/spikes/s4.md", fields: [("call", "call-1"), ("outcome", "ok")]);
    }
}
