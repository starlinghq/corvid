namespace Corvid.App.Core.Audit;

/// <summary>
/// The Audit area's seam to the engine (docs/plan/ui.md §4.12): the append-only event log per session or whole, the egress
/// ledger, detection matches, scans with the detection rules and trace export. The demo serves Audit.png's moment;
/// Corvid.App.Engine will read the event log, render requests through the privacy gate (G-9) and run the scanner.
/// Every call answers on the caller's thread or completes later; the view model awaits it on the UI thread.
/// </summary>
public interface IAuditService
{
    /// <summary>The log's size and location.</summary>
    Task<AuditLogInfo> GetLogAsync(CancellationToken cancellationToken);

    /// <summary>The sessions the log has events for, most recent first.</summary>
    Task<IReadOnlyList<AuditSession>> ListSessionsAsync(CancellationToken cancellationToken);

    /// <summary>A session's events, or the whole log's when <paramref name="sessionId"/> is null, newest first.</summary>
    Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(string? sessionId, CancellationToken cancellationToken);

    /// <summary>The egress ledger's entries for a session (or all), newest first.</summary>
    Task<IReadOnlyList<EgressRequest>> ListEgressAsync(string? sessionId, CancellationToken cancellationToken);

    /// <summary>The detection matches in a session (or all), newest first.</summary>
    Task<IReadOnlyList<DetectionMatch>> ListDetectionsAsync(string? sessionId, CancellationToken cancellationToken);

    /// <summary>The last scan of a session (or of the whole log), if one ran.</summary>
    Task<ScanResult?> GetLastScanAsync(string? sessionId, CancellationToken cancellationToken);

    /// <summary>Scans a session (or the whole log) with the detection rules; <paramref name="failOn"/> is the preset's severity.</summary>
    Task<ScanResult> ScanAsync(string? sessionId, string failOn, CancellationToken cancellationToken);

    /// <summary>Writes a Beacon JSONL trace of a session (or the whole log) to an owner-only file.</summary>
    Task<TraceExportResult> ExportAsync(string? sessionId, TraceExportOptions options, CancellationToken cancellationToken);
}
