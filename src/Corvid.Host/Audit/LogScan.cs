using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Corvid.Rules;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Audit;

/// <summary>
/// What a scan of the event log with the detection rules found (C-023): the sessions it read, each rule's findings and the
/// rules that could not be evaluated, and which matches the turn loop had already recorded inline.
/// </summary>
public sealed record LogScanResult(RulePack Pack, ImmutableArray<string> Sessions, ImmutableArray<DetectionFinding> Findings,
    ImmutableArray<DetectionError> Errors, ImmutableHashSet<(string Session, string Rule)> Recorded)
{
    /// <summary>Whether the turn loop already recorded this rule's match in the session (a <c>detection_matched</c> event).</summary>
    public bool RecordedInline(DetectionFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Recorded.Contains((finding.SessionId, finding.Rule.Id));
    }

    /// <summary>
    /// The exit code <c>corvid scan</c> gives for this result: <see cref="LogScan.FailureExitCode"/> when a rule could not be
    /// evaluated, <see cref="LogScan.FindingsExitCode"/> when a finding is at or above <paramref name="failOn"/>, else 0.
    /// </summary>
    public int ExitCode(string? failOn)
    {
        if (!Errors.IsEmpty)
            return LogScan.FailureExitCode;
        return failOn is not null && Findings.Any(finding => RuleSeverity.Rank(finding.Rule.Severity) >= RuleSeverity.Rank(failOn))
            ? LogScan.FindingsExitCode
            : 0;
    }
}

/// <summary>
/// The post-hoc scan of the event log with the built-in detection rules (C-023), moved here from <c>corvid scan</c> so the
/// CLI and the windowed app's Audit run the same scan. Read-only: it reads the log page by page and never appends.
/// </summary>
public static class LogScan
{
    /// <summary><c>corvid scan</c>'s exit code when a rule could not be evaluated (the CLI's general failure).</summary>
    public const int FailureExitCode = 1;

    /// <summary><c>corvid scan</c>'s exit code when a finding is at or above <c>--fail-on</c>.</summary>
    public const int FindingsExitCode = 4;

    private const int PageSize = 4096;

    /// <summary>
    /// The detection engine a scan uses: the built-in pack with the shell tools' mapping and the configured posture.
    /// </summary>
    /// <exception cref="RuleLoadException">The built-in rules did not load.</exception>
    public static DetectionEngine CreateEngine(CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DetectionEngine(RulePack.BuiltIn(), BuiltinTools.ShellToolMapping, options.Detections.Enforce);
    }

    /// <summary>
    /// Scans <paramref name="sessions"/> of the log (every session when null). A session the log does not have is an
    /// <see cref="ArgumentException"/>, named in its message.
    /// </summary>
    public static LogScanResult Run(SqliteEventLog log, DetectionEngine engine, IReadOnlyList<string>? sessions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(engine);
        ImmutableArray<string> known = [.. log.ListSessions(cancellationToken)];
        if (sessions is not null && sessions.FirstOrDefault(session => !known.Contains(session, StringComparer.Ordinal)) is { } unknown)
            throw new ArgumentException($"No session '{unknown}' in the event log.", nameof(sessions));
        var scanned = sessions is null ? known : [.. sessions];
        var findings = ImmutableArray.CreateBuilder<DetectionFinding>();
        var errors = ImmutableArray.CreateBuilder<DetectionError>();
        var recorded = ImmutableHashSet.CreateBuilder<(string, string)>();
        foreach (var id in scanned)
        {
            var events = Read(log, id, cancellationToken);
            foreach (var matched in events.Select(stored => stored.Payload).OfType<DetectionMatched>())
                recorded.Add((id, matched.RuleId));
            var scan = engine.Scan(events);
            findings.AddRange(scan.Findings);
            errors.AddRange(scan.Errors);
        }
        return new LogScanResult(engine.Pack, scanned, findings.ToImmutable(), errors.ToImmutable(), recorded.ToImmutable());
    }

    /// <summary>Every event of a session through the head captured first, read page by page.</summary>
    public static List<StoredEvent> Read(IEventLog log, string session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        var head = log.GetHead(session, cancellationToken);
        var events = new List<StoredEvent>();
        for (long after = 0; after < head;)
        {
            var page = log.Read(session, after, head, PageSize, cancellationToken);
            if (page.Count == 0)
                break;
            events.AddRange(page);
            after = page[^1].Sequence;
        }
        return events;
    }
}
