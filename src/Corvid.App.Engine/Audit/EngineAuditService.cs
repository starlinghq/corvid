using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Audit;
using Corvid.App.Core.Composition;
using Corvid.Host.Audit;
using Corvid.Host.Configuration;
using Corvid.Host.Sessions;
using Corvid.Kernel.Events;
using Corvid.Kernel.Traces;
using Corvid.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;

namespace Corvid.App.Engine.Audit;

/// <summary>
/// Audit over the event log (docs/plan/ui.md §4.12, F8, F10, F21). It reads the log file the configuration names, not the
/// runtime, so it answers while the models load and when they are missing; a log that is not there yet is empty and is not
/// created. Every read is synchronous SQLite, run off the UI thread.
/// <para>
/// A session's view numbers its events by the session's own positions, as <c>corvid scan</c> and the transcript's links
/// do. The whole log's view numbers every event by time across sessions (then by session and position), so each row has
/// its own number; a detection's events are numbered the same way there. The newest <see cref="MaxEvents"/> are listed.
/// </para>
/// <para>
/// Scans run the host's <see cref="LogScan"/> (the same as <c>corvid scan</c>); the last one per session lives in this
/// process. Traces are written by the host's <see cref="TraceExport"/> to <c>traces/</c> under the data directory, owner
/// only, never over an existing file.
/// </para>
/// <para>
/// The egress ledger (G-9) is the host's <see cref="EgressLedger"/>, read from its file like the log (never created here):
/// each request that left the machine, with its text as the gate sent it, reassembled on demand from what the ledger kept.
/// Its <c>egress_recorded</c> event in the session's log is the row that opens it, and places it in a turn and a step.
/// </para>
/// </summary>
public sealed class EngineAuditService : IAuditService
{
    /// <summary>The most events one view lists, newest first.</summary>
    public const int MaxEvents = 5000;

    /// <summary>The most egress requests one view lists, newest first.</summary>
    public const int MaxEgress = 500;

    /// <summary>The longest raw event shown; the trace export has it whole.</summary>
    public const int MaxJsonChars = 32 * 1024;

    private const int TextPreview = 160;

    private readonly AppEngine _engine;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ScanResult> _scans = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private SqliteEventLog? _log;
    private (long Signature, WholeLog Log)? _whole;

    public EngineAuditService(AppEngine engine, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _time = time;
    }

    public Task<AuditLogInfo> GetLogAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var path = LogPath();
        if (Open() is not { } log)
            return new AuditLogInfo(0, Display(path));
        var total = log.ListSessions(cancellationToken).Sum(session => log.GetHead(session, cancellationToken));
        return new AuditLogInfo(total, Display(path));
    }, cancellationToken);

    public Task<IReadOnlyList<AuditSession>> ListSessionsAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (Open() is not { } log)
            return (IReadOnlyList<AuditSession>)[];
        var sessions = new List<(AuditSession Session, DateTimeOffset Last)>();
        foreach (var id in log.ListSessions(cancellationToken))
        {
            string? title = null;
            var last = default(DateTimeOffset);
            foreach (var stored in SessionProjection.ReadRange(log, id, 0, log.GetHead(id, cancellationToken), cancellationToken))
            {
                last = stored.RecordedAt;
                if (title is null && stored.Payload is LoggedUserMessage user)
                    title = Line(user.Text, 80);
            }
            sessions.Add((new AuditSession(id, title ?? StoreTitle(id)), last));
        }
        return (IReadOnlyList<AuditSession>)[.. sessions.OrderByDescending(item => item.Last).Select(item => item.Session)];
    }, cancellationToken);

    public Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(string? sessionId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (Open() is not { } log)
            return (IReadOnlyList<AuditEvent>)[];
        // The egress_recorded rows open their request: linked by the ledger's number.
        var numbers = Ledger().List(sessionId, MaxEvents, cancellationToken).ToDictionary(entry => entry.RequestId, entry => entry.Number, StringComparer.Ordinal);
        AuditEvent Linked(AuditEvent row, StoredEvent stored) =>
            stored.Payload is EgressRecorded { Outcome: not EgressOutcomes.Refused } recorded && numbers.TryGetValue(recorded.RequestId, out var number)
                ? row with { EgressRequest = (int)number }
                : row;
        if (sessionId is not null)
        {
            var events = Session(log, sessionId, cancellationToken);
            var calls = Calls(events);
            return (IReadOnlyList<AuditEvent>)[.. events.OrderByDescending(item => item.Sequence).Take(MaxEvents)
                .Select(item => Linked(Describe(item, item.Sequence, calls), item))];
        }
        var whole = Whole(log, cancellationToken);
        return (IReadOnlyList<AuditEvent>)[.. whole.Ordered.Reverse().Take(MaxEvents)
            .Select(item => Linked(Describe(item.Event, item.Number, whole.Calls[item.Event.SessionId]), item.Event))];
    }, cancellationToken);

    /// <summary>
    /// The requests that left the machine, newest first (a refused one sent nothing, so it is not listed): each with its text
    /// as sent, reassembled from the ledger, and its place in the log (the <c>egress_recorded</c> event's number, the turn
    /// it was sent in and its step among that turn's requests to the same slot).
    /// </summary>
    public Task<IReadOnlyList<EgressRequest>> ListEgressAsync(string? sessionId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var settings = Options();
        var ledger = Ledger(settings);
        var entries = ledger.List(sessionId, MaxEgress, cancellationToken).Where(entry => entry.Left).ToList();
        if (entries.Count == 0)
            return (IReadOnlyList<EgressRequest>)[];
        var placed = new Dictionary<string, EgressPlace>(StringComparer.Ordinal);
        if (Open() is { } log)
        {
            if (sessionId is null)
            {
                var whole = Whole(log, cancellationToken);
                placed = Place(whole.Ordered.Select(item => item.Event), stored => whole.Numbers[(stored.SessionId, stored.Sequence)]);
            }
            else if (log.GetHead(sessionId, cancellationToken) > 0)
                placed = Place(Session(log, sessionId, cancellationToken), stored => stored.Sequence);
        }
        return (IReadOnlyList<EgressRequest>)[.. entries.Select(entry => Request(entry, placed.GetValueOrDefault(entry.RequestId), ledger, settings))];
    }, cancellationToken);

    /// <summary>Where a request sits in the log: its event's number, its turn and its step.</summary>
    private sealed record EgressPlace(long Sequence, int Turn, int Step, string? Unkept);

    /// <summary>
    /// Each request's place: a session's turns start at its user messages, and a turn's requests to one slot are its steps
    /// (the first generator call is step 1).
    /// </summary>
    private static Dictionary<string, EgressPlace> Place(IEnumerable<StoredEvent> events, Func<StoredEvent, long> number)
    {
        var placed = new Dictionary<string, EgressPlace>(StringComparer.Ordinal);
        foreach (var session in events.GroupBy(item => item.SessionId, StringComparer.Ordinal))
        {
            var turn = 0;
            var steps = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in session.OrderBy(item => item.Sequence))
            {
                switch (item.Payload)
                {
                    case LoggedUserMessage:
                        turn++;
                        steps.Clear();
                        break;
                    case EgressRecorded recorded when recorded.Outcome != EgressOutcomes.Refused:
                        var slot = recorded.Slot ?? "";
                        var step = steps[slot] = steps.GetValueOrDefault(slot) + 1;
                        placed[recorded.RequestId] = new EgressPlace(number(item), turn, step, recorded.Unkept);
                        break;
                }
            }
        }
        return placed;
    }

    /// <summary>
    /// One ledger entry as the page shows it. The text is what the ledger kept, reassembled; its excerpt and size are taken
    /// from it, never given separately. A request whose text could not be kept says so in its place.
    /// </summary>
    private static EgressRequest Request(EgressEntry entry, EgressPlace? place, EgressLedger ledger, CorvidOptions settings)
    {
        var full = entry.Kept ? ledger.Render(entry.RequestId) : null;
        full ??= "[not kept] The request as sent was not kept" + (place?.Unkept is { } why ? ": " + why : ".");
        // A remote model's profile is "<name>@<provider>"; the transcript calls it by its name, the provider by its model.
        var name = entry.Model.Split('@')[0];
        var model = settings.Models.Remote.TryGetValue(name, out var remote) && remote.Model is { Length: > 0 } configured ? configured : entry.Model;
        return new EgressRequest((int)entry.Number, place?.Sequence ?? 0, entry.SessionId ?? "", entry.At, model)
        {
            Slot = name,
            Turn = place?.Turn ?? 0,
            Step = place?.Step ?? 0,
            Secret = entry.Secret ?? "",
            Host = entry.Host ?? "",
            Bytes = EgressRendering.Size(full),
            Redacted = entry.Placeholders,
            RedactedSummary = [new AuditSpan(Redactions(entry))],
            Excerpt = EgressRendering.Excerpt(full),
            Full = full,
        };
    }

    /// <summary>"1 email value, 2 person values" for a redacted request; the gate's own words otherwise ("no PII found").</summary>
    internal static string Redactions(EgressEntry entry) =>
        entry.Outcome == EgressOutcomes.Redacted && !entry.Labels.IsEmpty
            ? string.Join(", ", entry.Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Value} {pair.Key} value{(pair.Value == 1 ? "" : "s")}")))
            : entry.Reason;

    /// <summary>The egress ledger, read from its file; never created here.</summary>
    private EgressLedger Ledger(CorvidOptions? settings = null) => new(CorvidPaths.EgressDatabase(settings ?? Options()));

    public Task<IReadOnlyList<DetectionMatch>> ListDetectionsAsync(string? sessionId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (Open() is not { } log)
            return (IReadOnlyList<DetectionMatch>)[];
        if (sessionId is not null)
            return (IReadOnlyList<DetectionMatch>)[.. Session(log, sessionId, cancellationToken).Where(item => item.Payload is DetectionMatched)
                .OrderByDescending(item => item.Sequence).Select(item => Match(item, sequence => sequence))];
        var whole = Whole(log, cancellationToken);
        return (IReadOnlyList<DetectionMatch>)[.. whole.Ordered.Where(item => item.Event.Payload is DetectionMatched).Reverse()
            .Select(item => Match(item.Event, sequence => whole.Numbers.GetValueOrDefault((item.Event.SessionId, sequence), 0)))];
    }, cancellationToken);

    public Task<ScanResult?> GetLastScanAsync(string? sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(_scans.TryGetValue(sessionId ?? "", out var scan) ? scan : null);

    public Task<ScanResult> ScanAsync(string? sessionId, string failOn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failOn);
        if (RuleSeverity.Rank(failOn) < 0)
            throw new ArgumentException($"'{failOn}' is not a severity: info, low, medium, high or critical.", nameof(failOn));
        return Task.Run(() =>
        {
            if (Open() is not { } log)
                throw new InvalidOperationException($"There is no event log at {Display(LogPath())} yet, so there is nothing to scan.");
            DetectionEngine engine;
            try
            {
                engine = LogScan.CreateEngine(Options());
            }
            catch (RuleLoadException exception)
            {
                throw new InvalidOperationException("The detection rules did not load: " + exception.Message, exception);
            }
            LogScanResult result;
            try
            {
                result = LogScan.Run(log, engine, sessionId is null ? null : [sessionId], cancellationToken);
            }
            catch (ArgumentException exception)
            {
                throw new KeyNotFoundException(exception.Message, exception);
            }
            if (!result.Errors.IsEmpty)
                throw new InvalidOperationException(string.Join(" ", result.Errors.Select(error =>
                    $"{error.Rule.Id} could not be evaluated in session {error.SessionId}: {error.Message}")));
            int Count(params string[] severities) => result.Findings.Count(finding => severities.Contains(finding.Rule.Severity, StringComparer.Ordinal));
            // The page has three counts: a critical finding counts as high; an informational one is not counted.
            var scan = new ScanResult(_time.GetUtcNow(), Count(RuleSeverity.High, RuleSeverity.Critical), Count(RuleSeverity.Medium),
                Count(RuleSeverity.Low), failOn, result.ExitCode(failOn));
            _scans[sessionId ?? ""] = scan;
            return scan;
        }, cancellationToken);
    }

    public Task<TraceExportResult> ExportAsync(string? sessionId, TraceExportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(async () =>
        {
            if (Open() is not { } log)
                throw new InvalidOperationException($"There is no event log at {Display(LogPath())} yet, so there is nothing to export.");
            var settings = Options();
            IReadOnlyList<string> sessions;
            if (sessionId is null)
                sessions = log.ListSessions(cancellationToken);
            else if (log.GetHead(sessionId, cancellationToken) == 0)
                throw new KeyNotFoundException($"Session '{sessionId}' has no events.");
            else
                sessions = [sessionId];
            var retention = options.Retention == TraceRetention.MetadataOnly ? BeaconRetention.Metadata : BeaconRetention.Full;
            var export = TraceExport.Options(settings, retention, options.IncludeLocalMemory);
            var directory = TraceExport.Directory(settings);
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var stamp = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = $"{DemoAuditService.FileNamePart(sessionId ?? "all")}-{stamp}";
            for (var attempt = 1; ; attempt++)
            {
                var path = Path.Combine(directory, attempt == 1 ? name + ".jsonl" : FormattableString.Invariant($"{name}-{attempt}.jsonl"));
                FileStream file;
                try
                {
                    file = TraceExport.CreateOwnerOnly(path);
                }
                catch (IOException) when (File.Exists(path) && attempt < 100)
                {
                    continue;
                }
                int count;
                try
                {
                    await using (file.ConfigureAwait(false))
                    {
                        TraceFileCreated?.Invoke(path);
                        count = await TraceExport.WriteAsync(log, sessions, export, file, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    // Cancelled or failed part-way: a partial trace would read as a whole one, so it goes.
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"corvid app: the partial trace {path} could not be removed: {exception.Message}");
                    }
                    throw;
                }
                return new TraceExportResult(Display(path), count, new FileInfo(path).Length, options.Retention);
            }
        }, cancellationToken);
    }

    /// <summary>Test seam: runs with the trace's path once its file is created, before anything is written to it.</summary>
    internal Action<string>? TraceFileCreated { get; set; }

    private CorvidOptions Options()
    {
        try
        {
            return _engine.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        }
        catch (OptionsValidationException exception)
        {
            throw new ServiceUnavailableException(_engine.StartFailure ?? "Corvid's configuration is not valid: " + string.Join(" ", exception.Failures));
        }
    }

    private string LogPath() => CorvidPaths.EventLog(Options());

    /// <summary>The log, opened once; null while there is none (opening would create it).</summary>
    private SqliteEventLog? Open()
    {
        lock (_gate)
        {
            if (_log is not null)
                return _log;
            var path = LogPath();
            return File.Exists(path) ? _log = new SqliteEventLog(path) : null;
        }
    }

    private static List<StoredEvent> Session(SqliteEventLog log, string sessionId, CancellationToken cancellationToken)
    {
        var head = log.GetHead(sessionId, cancellationToken);
        if (head == 0)
            throw new KeyNotFoundException($"There is no session '{sessionId}' in the event log.");
        return [.. SessionProjection.ReadRange(log, sessionId, 0, head, cancellationToken)];
    }

    /// <summary>The whole log ordered by time, numbered from 1; read again only when a session's head has moved.</summary>
    private WholeLog Whole(SqliteEventLog log, CancellationToken cancellationToken)
    {
        var sessions = log.ListSessions(cancellationToken);
        var heads = sessions.Select(session => (session, log.GetHead(session, cancellationToken))).ToList();
        var signature = heads.Aggregate(17L, (hash, item) => unchecked(hash * 31 + item.Item2 * 7 + item.session.GetHashCode(StringComparison.Ordinal)));
        lock (_gate)
            if (_whole is { } cached && cached.Signature == signature)
                return cached.Log;
        var events = heads.SelectMany(item => SessionProjection.ReadRange(log, item.session, 0, item.Item2, cancellationToken))
            .OrderBy(item => item.RecordedAt).ThenBy(item => item.SessionId, StringComparer.Ordinal).ThenBy(item => item.Sequence).ToList();
        var ordered = events.Select((item, index) => (Event: item, Number: (long)index + 1)).ToImmutableArray();
        var numbers = ordered.ToImmutableDictionary(item => (item.Event.SessionId, item.Event.Sequence), item => item.Number);
        var calls = events.GroupBy(item => item.SessionId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => Calls(group), StringComparer.Ordinal);
        var whole = new WholeLog(ordered, numbers, calls);
        lock (_gate)
            _whole = (signature, whole);
        return whole;
    }

    private sealed record WholeLog(ImmutableArray<(StoredEvent Event, long Number)> Ordered,
        ImmutableDictionary<(string Session, long Sequence), long> Numbers, ImmutableDictionary<string, ImmutableDictionary<string, ToolCallIssued>> Calls);

    private static ImmutableDictionary<string, ToolCallIssued> Calls(IEnumerable<StoredEvent> events) =>
        events.Select(item => item.Payload).OfType<ToolCallIssued>().GroupBy(call => call.CallId, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

    private static DetectionMatch Match(StoredEvent stored, Func<long, long> number)
    {
        var matched = (DetectionMatched)stored.Payload;
        return new DetectionMatch(matched.RuleId, matched.RuleVersion, matched.Severity, matched.Posture, number(stored.Sequence),
            [.. matched.Sequences.Select(number)])
        {
            SessionId = stored.SessionId,
            Enforced = matched.Enforced == "ask",
            Explanation = [new AuditSpan(matched.Reason)],
        };
    }

    /// <summary>One stored event as the table shows it: built span by span, with what came from a model, tool or file in plain spans.</summary>
    internal static AuditEvent Describe(StoredEvent stored, long number, IReadOnlyDictionary<string, ToolCallIssued> calls)
    {
        var payload = stored.Payload;
        var type = EventPayload.Type(payload);
        string? tool = null, path = null;
        var alert = false;
        AuditSpan P(string text) => new(text);
        AuditSpan M(string text) => new(text, AuditSpanStyle.Mono);
        ImmutableArray<AuditSpan> summary;
        switch (payload)
        {
            case LoggedUserMessage user:
                summary = [P("user: "), P(Line(user.Text, TextPreview))];
                break;
            case AssistantMessage answer:
                summary = [P("answer: "), P(Line(answer.Text, TextPreview))];
                break;
            case ToolCallIssued call:
                (tool, path) = (call.ToolName, Subject(call));
                summary = path is null ? [M(call.ToolName)] : [M(call.ToolName), P(" "), M(Line(path, TextPreview))];
                break;
            case ToolResult result:
                if (calls.TryGetValue(result.CallId, out var issued))
                    (tool, path) = (issued.ToolName, Subject(issued));
                summary = [P(result.IsError ? "failed: " : "result: "), M(tool ?? result.CallId), P(" · "),
                    P(Line(ToolPresentation.ResultText(result.Result), TextPreview))];
                break;
            case Decision decision:
                summary = [P("decided "), M(decision.QuestionId), P(" · "), M(decision.Action),
                    P(string.Create(CultureInfo.InvariantCulture, $" at {decision.Confidence:0.00} (floor {decision.Threshold:0.00})"))];
                break;
            case DecisionOutcomeRecorded outcome:
                summary = [P("outcome of "), M(outcome.DecisionId), P(": "), M(outcome.Outcome)];
                break;
            case ApprovalRequested asked:
                summary = [P("approval asked: "), P(Line(asked.Reason, TextPreview))];
                break;
            case ApprovalResolved resolved:
                summary = [P(resolved.Approved ? "approved" : "denied"), P(" · "), P(Line(resolved.Reason, TextPreview))];
                break;
            case ModelSwitched switched:
                summary = [M(switched.Slot), P(" switched model: "), P(Line(switched.Reason, TextPreview))];
                break;
            case Compacted compacted:
                summary = [P("compacted events "), M(string.Create(CultureInfo.InvariantCulture, $"{compacted.FromSequence}–{compacted.ThroughSequence}"))];
                break;
            case CompactionExpanded expanded:
                summary = [P("compaction at "), M(expanded.CompactedSequence.ToString(CultureInfo.InvariantCulture)), P(" expanded")];
                break;
            case MemoryWritten memory:
                summary = [P("memory "), M(memory.Operation), P(" "), M(memory.MemoryId)];
                break;
            case SkillChanged skill:
                summary = [P("skill "), M(skill.SkillId), P(" changed by "), P(skill.Author)];
                break;
            case GoalSet goal:
                summary = goal.Goal is { } text ? [P("goal: "), P(Line(text, TextPreview))] : [P("goal cleared")];
                break;
            case TodosSet todos:
                summary = [P(string.Create(CultureInfo.InvariantCulture, $"task list: {todos.Items.Count(item => item.Completed)} of {todos.Items.Length} done"))];
                break;
            case CheckpointTaken checkpoint:
                var paths = checkpoint.Paths ?? [];
                path = paths.FirstOrDefault();
                summary = [M(checkpoint.Kind ?? "checkpoint"), P(" checkpoint "), M(checkpoint.CheckpointId),
                    .. paths.IsEmpty ? Array.Empty<AuditSpan>() : [P(" · "), M(Line(string.Join(", ", paths.Take(4)), TextPreview)),
                        P(paths.Length > 4 ? string.Create(CultureInfo.InvariantCulture, $" and {paths.Length - 4} more") : "")]];
                break;
            case SessionRewound rewound:
                summary = [M(rewound.Reason), P(" to event "), M(rewound.ThroughSequence.ToString(CultureInfo.InvariantCulture)),
                    P(string.Create(CultureInfo.InvariantCulture, $" · {rewound.Restored.Length} file(s) put back"))];
                break;
            case DetectionMatched matched:
                alert = true;
                summary = [M(matched.RuleId), P(string.Create(CultureInfo.InvariantCulture, $" v{matched.RuleVersion} · {matched.Severity} · {matched.Posture} · ")),
                    P(Line(matched.Reason, TextPreview))];
                break;
            case DeliveryQueued queued:
                summary = [P("delivery queued to "), M(queued.Adapter), P(" · "), P(Line(queued.Text, TextPreview))];
                break;
            case DeliveryResolved delivered:
                summary = [P("delivery "), M(delivered.DeliveryId), P(" "), M(delivered.Status)];
                break;
            case McpToolHeld held:
                (tool, summary) = ($"{held.Server}__{held.Tool}", [P("MCP tool held: "), M($"{held.Server}/{held.Tool}"), P(" · "), M(held.Reason)]);
                break;
            case McpToolAccepted accepted:
                (tool, summary) = ($"{accepted.Server}__{accepted.Tool}", [P("MCP tool accepted: "), M($"{accepted.Server}/{accepted.Tool}")]);
                break;
            case McpPinsForgotten forgotten:
                summary = [P("MCP pins forgotten for "), M(forgotten.Server)];
                break;
            case ProjectBound bound:
                summary = [P("bound to project "), M(bound.ProjectId[..Math.Min(12, bound.ProjectId.Length)]), P(" from its " + bound.Source)];
                break;
            case EgressRecorded egress:
                tool = egress.Slot;
                var sentTo = egress.Host is { } host ? $" ({host})" : "";
                summary = egress.Outcome switch
                {
                    EgressOutcomes.Refused => [P("refused before it left: "), M(egress.Model), P(sentTo + " · "), P(Line(egress.Reason, TextPreview))],
                    EgressOutcomes.Redacted => [P("sent to "), M(egress.Model), P(sentTo + string.Create(CultureInfo.InvariantCulture,
                        $" · {egress.Placeholders} {(egress.Placeholders == 1 ? "value" : "values")} redacted · {AuditText.Size(egress.Bytes)}"))],
                    _ => [P("sent to "), M(egress.Model), P(sentTo + " · " + AuditText.Size(egress.Bytes) + " · "), P(Line(egress.Reason, TextPreview))],
                };
                break;
            case ApprovalAssessed assessed:
                var allowed = assessed.Resolution is ApprovalResolutions.Automatic or ApprovalResolutions.RuleAllowed;
                summary = [P(allowed ? "ran unasked: " : "refused unasked: "), M(assessed.Resolution), P($" in {assessed.Mode} mode"),
                    .. assessed.Rule is { } rule ? new[] { P(" · rule "), P(Line(rule, TextPreview)) } : [],
                    .. assessed.Risk is { Level: { } level } ? new[] { P(string.Create(CultureInfo.InvariantCulture, $" · risk {level}")) } : []];
                break;
            default:
                summary = [M(type)];
                break;
        }

        return new AuditEvent(number, stored.SessionId, stored.RecordedAt, type, Group(payload))
        {
            Summary = summary,
            Tool = tool,
            Path = path,
            IsAlert = alert,
            Json = Indented(EventPayload.Json(payload)),
        };
    }

    private static AuditEventGroup Group(SessionEvent payload) => payload switch
    {
        ToolCallIssued or ToolResult or McpToolHeld or McpToolAccepted or McpPinsForgotten => AuditEventGroup.Tools,
        ApprovalRequested or ApprovalResolved or ApprovalAssessed => AuditEventGroup.Approvals,
        Decision or DecisionOutcomeRecorded => AuditEventGroup.Decisions,
        // A request that left for a model sits with the model switches, as an escalation's request does in Audit.png.
        ModelSwitched or EgressRecorded => AuditEventGroup.ModelSwitches,
        DetectionMatched => AuditEventGroup.Detections,
        CheckpointTaken or SessionRewound => AuditEventGroup.Checkpoints,
        MemoryWritten or SkillChanged or ProjectBound => AuditEventGroup.MemoryAndSkills,
        CompactionExpanded => AuditEventGroup.Compaction,
        _ => AuditEventGroup.Messages,
    };

    /// <summary>What a call acts on, for the "path or tool" filter: its path, command or URL.</summary>
    private static string? Subject(ToolCallIssued call) => ToolPresentation.Target(call.Arguments);

    private static string Indented(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            document.WriteTo(writer);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return text.Length <= MaxJsonChars
            ? text
            : string.Concat(text.AsSpan(0, MaxJsonChars), string.Create(CultureInfo.InvariantCulture,
                $"\n… {text.Length - MaxJsonChars:N0} more characters; a full trace export has the event whole."));
    }

    private static string Line(string text, int length)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > length ? line[..(length - 1)] + "…" : line;
    }

    /// <summary>A store's own session, named for the picker.</summary>
    private static string StoreTitle(string id) => id switch
    {
        McpSessions.Pins => "MCP tool pins",
        Corvid.Gateway.GatewaySessions.Ledger => "gateway delivery ledger",
        _ => "no messages",
    };

    /// <summary>A path as the page shows it: under the home directory as <c>~/…</c>.</summary>
    private static string Display(string path)
    {
        var home = CorvidPaths.Home;
        return home.Length > 1 && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}
