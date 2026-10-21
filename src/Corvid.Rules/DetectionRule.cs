using System.Collections.Immutable;
using Corvid.Abstractions.Events;

namespace Corvid.Rules;

/// <summary>Rule severities, lowest first (the wire names Beacon's format uses).</summary>
public static class RuleSeverity
{
    public const string Info = "info";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static ImmutableArray<string> All { get; } = [Info, Low, Medium, High, Critical];

    /// <summary>0 for info up to 4 for critical; -1 for an unknown name.</summary>
    public static int Rank(string severity) => All.IndexOf(severity);
}

/// <summary>A rule's maturity, which sets what its embedded tests must prove.</summary>
public static class RuleStatus
{
    /// <summary>At least one embedded test.</summary>
    public const string Experimental = "experimental";
    /// <summary>At least one <c>match</c> and one <c>no_match</c> test.</summary>
    public const string Stable = "stable";
    /// <summary>Must still load; tests are optional.</summary>
    public const string Deprecated = "deprecated";
}

/// <summary>What a match does at pre_execute.</summary>
public static class RulePosture
{
    /// <summary>Records only.</summary>
    public const string Detect = "detect";
    /// <summary>Records and forces an approval prompt (never a denial in v1, never an allow).</summary>
    public const string Enforce = "enforce";
}

/// <summary>A loaded, validated detection rule (C-023). Exactly one of a single-event match or a correlation.</summary>
public sealed class DetectionRule
{
    internal DetectionRule(string id, int version, string title, string? description, string severity, string status, string posture,
        string reason, string category, RuleStep? match, RuleCorrelation? correlation, ImmutableArray<RuleTest> tests)
    {
        Id = id;
        Version = version;
        Title = title;
        Description = description;
        Severity = severity;
        Status = status;
        Posture = posture;
        Reason = reason;
        Category = category;
        Match = match;
        Correlation = correlation;
        Tests = tests;
    }

    public string Id { get; }

    public int Version { get; }

    public string Title { get; }

    public string? Description { get; }

    /// <summary>A <see cref="RuleSeverity"/> name.</summary>
    public string Severity { get; }

    /// <summary>A <see cref="RuleStatus"/> name.</summary>
    public string Status { get; }

    /// <summary>A <see cref="RulePosture"/> name, as declared.</summary>
    public string Posture { get; }

    public string Reason { get; }

    /// <summary>The pack directory the rule belongs to (Beacon's categories: credential-access, context-exfiltration, ...).</summary>
    public string Category { get; }

    public bool IsCorrelation => Correlation is not null;

    public ImmutableArray<RuleTest> Tests { get; }

    internal RuleStep? Match { get; }

    internal RuleCorrelation? Correlation { get; }

    internal ImmutableArray<RuleStep> Steps => Match is { } match ? [match] : Correlation!.Steps;

    public override string ToString() => $"{Id} v{Version}";
}

/// <summary>An embedded test: the events, in the persisted form, and whether the rule must match them.</summary>
public sealed record RuleTest(string Name, bool ExpectMatch, ImmutableArray<StoredEvent> Events);

/// <summary>One step: an event type and a predicate over that event's fields.</summary>
internal sealed record RuleStep(string Id, string Event, Predicate Where);

/// <param name="Sequence">Steps must occur in the listed order; otherwise in any order.</param>
internal sealed record RuleCorrelation(TimeSpan Window, bool Sequence, ImmutableArray<RuleStep> Steps);

/// <summary>Raised when a rule file or pack does not load. The message names the file and what is wrong.</summary>
public sealed class RuleLoadException(string file, string message, Exception? inner = null)
    : Exception($"{file}: {message}", inner)
{
    /// <summary>The rule file (or pack) that failed.</summary>
    public string File { get; } = file;
}
