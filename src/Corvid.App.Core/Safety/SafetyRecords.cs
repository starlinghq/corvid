using System.Collections.Immutable;

namespace Corvid.App.Core.Safety;

/// <summary>Whether a remembered rule lets a call run unasked or refuses it.</summary>
public enum RuleDecision
{
    Allow,
    Deny,
}

/// <summary>How long a remembered rule lasts: the session it was written in, or every session (corvid.json).</summary>
public enum RuleScope
{
    Session,
    Always,
}

/// <summary>
/// A remembered approval rule (docs/plan/ui.md §4.4): what an answer of "this session" or "always" wrote, or one already in
/// corvid.json. Listed with its scope and where it came from, and removable (K-7).
/// </summary>
/// <param name="Id">Stable for the rule's life; the removal names it.</param>
/// <param name="Text">
/// The rule in the engine's words, with commands and paths in backticks ("`dotnet test` in workspace"). It comes from the
/// engine and, through the command, from the model: it is data, cleaned for display and revealed where a person acts on it.
/// </param>
public sealed record SafetyRule(string Id, RuleDecision Decision, RuleScope Scope, string Text)
{
    /// <summary>The session a session rule belongs to, or the session whose approval wrote an always rule.</summary>
    public string? SessionId { get; init; }

    /// <summary>That session's title, as the rail lists it.</summary>
    public string? SessionTitle { get; init; }

    /// <summary>The approval whose answer wrote it ("a-32").</summary>
    public string? ApprovalId { get; init; }

    /// <summary>The turn of that approval.</summary>
    public int? Turn { get; init; }

    /// <summary>Where it lives when no approval in a listed session wrote it ("corvid.json").</summary>
    public string? Origin { get; init; }

    /// <summary>When it was written, when known.</summary>
    public DateTimeOffset? WrittenAt { get; init; }

    /// <summary>Calls it has let run (or refused) since it was written.</summary>
    public int Uses { get; init; }
}

/// <summary>A detection rule's severity, as the pack names it.</summary>
public enum DetectionSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// A rule of a detection pack (Corvid.Rules): its id and version, what it finds, its severity, and its posture: <c>enforce</c>
/// forces an ask when it matches a pending call, <c>detect</c> only records.
/// </summary>
public sealed record DetectionRule(string Id, int Version, string Title, DetectionSeverity Severity, bool Enforces)
{
    /// <summary>The pack's folder for it ("context-exfiltration").</summary>
    public string Category { get; init; } = "";
}

/// <summary>
/// A pack of detection rules and its switch: with <see cref="Enforce"/> off (<c>Detections:Enforce=false</c>) every rule
/// records only, whatever its own posture says.
/// </summary>
public sealed record DetectionPack(string Name, bool Enforce, ImmutableArray<DetectionRule> Rules);

/// <summary>A match of a detection rule (C-023): the events that completed it and what it did.</summary>
/// <param name="Sequence">The <c>detection_matched</c> event's sequence in the log.</param>
/// <param name="Events">The events that matched, by sequence; Audit filters the log to them.</param>
public sealed record SafetyDetection(string RuleId, DetectionSeverity Severity, string SessionId, DateTimeOffset At, long Sequence, ImmutableArray<long> Events)
{
    /// <summary>It forced an approval prompt; otherwise it was recorded only.</summary>
    public bool Enforced { get; init; }

    /// <summary>The prompt it forced, and how it was answered ("a-31 · denied").</summary>
    public string? Outcome { get; init; }

    /// <summary>What happened, in a sentence; backticks mark commands. Data from the log.</summary>
    public string Summary { get; init; } = "";
}

/// <summary>One code of the hardline list: what Corvid refuses in every mode, whatever a rule or a score says.</summary>
public sealed record HardlineCode(string Code, string Sentence);

/// <summary>
/// A session's taint (docs/plan/ui.md §4.4, F9): what outside content entered it and when, and when the kernel cleared it.
/// </summary>
public sealed record SessionTaint(string SessionId, string Title, bool Tainted)
{
    /// <summary>What brought it in ("web_fetch docs.example.org"). Data.</summary>
    public string? Source { get; init; }

    public DateTimeOffset? Since { get; init; }

    /// <summary>When the kernel cleared it, for a session tainted earlier and clean now.</summary>
    public DateTimeOffset? ClearedAt { get; init; }

    /// <summary>Another process drives the session; this window cannot change its mode.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>Everything the Safety page shows from the engine, read at once.</summary>
public sealed record SafetySnapshot(
    ImmutableArray<SafetyRule> Rules,
    ImmutableArray<DetectionPack> Packs,
    ImmutableArray<SafetyDetection> Detections,
    ImmutableArray<HardlineCode> Hardline,
    ImmutableArray<SessionTaint> Taint)
{
    /// <summary>Matches found but left out of <see cref="Detections"/>, which holds the latest only: older ones, for Audit.</summary>
    public int MoreDetections { get; init; }

    /// <summary>Sessions whose logs were not searched for matches (only the most recently active are).</summary>
    public int SessionsNotScanned { get; init; }

    /// <summary>Every match found, listed or not.</summary>
    public int DetectionCount => Detections.Length + MoreDetections;
}
