namespace Corvid.App.Core.Shell;

/// <summary>What a link into another area points at, so the area lands on it rather than where it was left.</summary>
public enum AreaLinkTarget
{
    /// <summary>The area itself, showing the session the link names.</summary>
    Session,

    /// <summary>What left the machine: the session's latest request in the egress ledger (docs/plan/ui.md F8).</summary>
    Egress,

    /// <summary>A detection match: the log filtered to the events that completed the rule (docs/plan/ui.md F10).</summary>
    Detection,
}

/// <summary>
/// A link from one area into another ("what left the machine", "Open in Audit"): the area, the session it concerns and
/// what in it. <see cref="ShellViewModel.Follow"/> shows the area and publishes the link; the area's view model reads it.
/// </summary>
public sealed record AreaLink(Area Area, string? SessionId, AreaLinkTarget Target = AreaLinkTarget.Session)
{
    /// <summary>The events the link names, by sequence (a detection's matched events).</summary>
    public IReadOnlyList<long> Events { get; init; } = [];

    /// <summary>A name for what is linked (a detection rule's id).</summary>
    public string? Name { get; init; }
}
