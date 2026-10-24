using System.Collections.Immutable;

namespace Corvid.App.Core.Audit;

/// <summary>
/// The filter groups of the event log (Audit.png's Filter panel), in the order the panel lists them. Every event type
/// belongs to exactly one, so a group's box shows or hides all of its types.
/// </summary>
public enum AuditEventGroup
{
    /// <summary>The conversation: user and assistant messages, questions, and the compaction record that replaced earlier ones.</summary>
    Messages,

    /// <summary><c>tool_call_issued</c> and <c>tool_result</c>.</summary>
    Tools,

    /// <summary><c>approval_requested</c> and <c>approval_resolved</c>.</summary>
    Approvals,

    /// <summary>The decider's answers at a checkpoint (<c>decision</c>).</summary>
    Decisions,

    /// <summary>A slot changed model (<c>model_switched</c>): escalations, fallbacks, a refused cloud model.</summary>
    ModelSwitches,

    /// <summary>A detection rule matched (<c>detection_matched</c>).</summary>
    Detections,

    /// <summary>The rewind ledger's file checkpoints (<c>checkpoint_recorded</c>).</summary>
    Checkpoints,

    /// <summary>Recalled and staged memory, skills used or drafted.</summary>
    MemoryAndSkills,

    /// <summary>The summarizer's working events (<c>compaction_chunk</c>); the record itself is a message.</summary>
    Compaction,
}

/// <summary>A session the log has events for, as the session picker lists it.</summary>
public sealed record AuditSession(string Id, string Title);

/// <summary>The event log as a whole: how many events it holds and where it lives ("1,214 events · append-only · …").</summary>
public sealed record AuditLogInfo(long TotalEvents, string Path);

/// <summary>How a span of an event's summary is set.</summary>
public enum AuditSpanStyle
{
    Plain,

    /// <summary>Commands, rule ids and values, in monospace.</summary>
    Mono,

    /// <summary>A link within the app (the row's egress request); it selects the row.</summary>
    Link,
}

/// <summary>A span of an event's one-line summary.</summary>
public sealed record AuditSpan(string Text, AuditSpanStyle Style = AuditSpanStyle.Plain);

/// <summary>
/// One typed event of the append-only log (docs/plan/ui.md §4.12): its sequence, session, time, type and group, the
/// summary the table shows, and the raw event as stored.
/// </summary>
/// <param name="Sequence">The log's sequence number; unique and increasing across sessions.</param>
/// <param name="Type">The event's type as stored: <c>tool_call_issued</c>, <c>approval_resolved</c>…</param>
public sealed record AuditEvent(long Sequence, string SessionId, DateTimeOffset At, string Type, AuditEventGroup Group)
{
    /// <summary>
    /// The one-line summary as spans. The engine builds them structurally, a span per piece, with what came from the model,
    /// a tool or a file in plain spans; it never parses untrusted text as markup (<see cref="AuditText.Parse"/> is for the
    /// app's own wording, with untrusted pieces put in through <see cref="AuditText.Escape"/>).
    /// </summary>
    public ImmutableArray<AuditSpan> Summary { get; init; } = [];

    /// <summary>The tool the event concerns, for the "path or tool" filter.</summary>
    public string? Tool { get; init; }

    /// <summary>The path or address the event concerns, for the "path or tool" filter.</summary>
    public string? Path { get; init; }

    /// <summary>The egress ledger's request this event sent to a cloud model, if it did.</summary>
    public int? EgressRequest { get; init; }

    /// <summary>A detection match: the table draws the row in red.</summary>
    public bool IsAlert { get; init; }

    /// <summary>The event as the log stores it, as indented JSON.</summary>
    public string Json { get; init; } = "{}";
}

/// <summary>
/// One request that left the machine for a cloud model (the egress ledger, docs/plan/ui.md §4.12, F8): when and for which
/// step, the model and the route, how much was sent, what the privacy gate redacted, and the request as sent, rendered on
/// demand by the same gate (G-9).
/// <para>
/// For the engine: the gate should hand its redactions over as structure (where each one sits in <see cref="Full"/> and
/// what kind it is), not only as <c>[redacted:kind]</c> markers in the text, which a tool's output or the model can
/// imitate; until then the page checks the markers it finds against <see cref="Redacted"/> and warns when they differ.
/// <see cref="Excerpt"/> and <see cref="Bytes"/> should be derived from <see cref="Full"/> (its start and end, its UTF-8
/// size), never supplied separately, so the excerpt cannot show something other than what was sent.
/// </para>
/// </summary>
/// <param name="Number">The request's number in the ledger ("request 7").</param>
/// <param name="Sequence">The event that sent it (the <c>model_switched</c> or generator call).</param>
public sealed record EgressRequest(int Number, long Sequence, string SessionId, DateTimeOffset At, string Model)
{
    /// <summary>The model's short name, as the transcript's escalation row says it ("claude").</summary>
    public string Slot { get; init; } = "";

    public int Turn { get; init; }

    public int Step { get; init; }

    /// <summary>The Vault secret the request authenticated with; the app never sees its value.</summary>
    public string Secret { get; init; } = "";

    public string Host { get; init; } = "";

    public long Bytes { get; init; }

    public int Tokens { get; init; }

    public int CachedPrefix { get; init; }

    /// <summary>What the gate redacted, one entry per value ("one email address").</summary>
    public ImmutableArray<AuditSpan> RedactedSummary { get; init; } = [];

    public int Redacted { get; init; }

    /// <summary>The start and end of the request as sent, with an ellipsis line where the middle was left out.</summary>
    public string Excerpt { get; init; } = "";

    /// <summary>The whole request as sent, after the gate.</summary>
    public string Full { get; init; } = "";
}

/// <summary>
/// A detection rule's match (docs/plan/ui.md §4.4, F10): the rule, its pack and version, the severity and posture, what
/// matched and what the rule did.
/// </summary>
/// <param name="Sequence">The <c>detection_matched</c> event.</param>
/// <param name="Events">The events that completed the rule, oldest first.</param>
public sealed record DetectionMatch(string RuleId, int Version, string Severity, string Posture, long Sequence, ImmutableArray<long> Events)
{
    public string SessionId { get; init; } = "";

    public string Pack { get; init; } = "built-in pack";

    /// <summary>The match forced an approval prompt (enforce); otherwise it was recorded only.</summary>
    public bool Enforced { get; init; }

    /// <summary>What happened, in a sentence or two.</summary>
    public ImmutableArray<AuditSpan> Explanation { get; init; } = [];
}

/// <summary>
/// A scan of the log with the detection rules (<c>corvid audit scan</c>): when, the counts by severity, and the exit code
/// the command would give with the chosen <c>--fail-on</c> preset.
/// </summary>
public sealed record ScanResult(DateTimeOffset At, int High, int Medium, int Low, string FailOn, int ExitCode);

/// <summary>How much of each event a trace keeps (Beacon JSONL).</summary>
public enum TraceRetention
{
    /// <summary>Full content, secrets redacted, strings cut at 4 KiB.</summary>
    Full,

    /// <summary>Names, paths, commands and decisions only.</summary>
    MetadataOnly,
}

/// <summary>The Export panel's choices.</summary>
public sealed record TraceExportOptions(TraceRetention Retention, bool IncludeLocalMemory);

/// <summary>A written trace: where, how many events, how large, with which retention.</summary>
/// <param name="Written">False when nothing was written to disk (the demo only says what the engine would write).</param>
public sealed record TraceExportResult(string Path, int Events, long Bytes, TraceRetention Retention, bool Written = true);
