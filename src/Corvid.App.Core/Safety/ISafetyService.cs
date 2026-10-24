using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Safety;

/// <summary>
/// The Safety area's seam to the engine (docs/plan/ui.md §4.4): remembered rules, the detection packs and their matches,
/// the hardline list and each session's taint. The demo implements it now; Corvid.App.Engine will over the approval
/// pipeline's rule store (K-7), the detection engine and the event log. Calls are made on the UI thread and
/// <see cref="Changed"/> is raised on it.
/// </summary>
public interface ISafetyService
{
    /// <summary>Something the page shows changed: a rule written or removed, a session tainted or cleared, a match.</summary>
    event EventHandler? Changed;

    Task<SafetySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a remembered rule (K-7): a session rule from its session, an always rule from corvid.json. Calls it allowed
    /// ask again from the next one. A rule that is already gone changes nothing.
    /// </summary>
    Task RemoveRuleAsync(string ruleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a pack's enforce switch (<c>Detections:Enforce</c>). Off, every rule of the pack records only. A safety
    /// configuration change: the engine writes it from the app, never from a tool call (hardline
    /// <c>safety_configuration_change</c>).
    /// </summary>
    Task SetEnforceAsync(string pack, bool enforce, CancellationToken cancellationToken = default);
}

/// <summary>
/// The session the Safety page's mode control drives: the one selected in the rail. The composition adapts the session
/// hub to it; tests pass their own.
/// </summary>
public interface ISafetySession
{
    /// <summary>The current session changed, or its mode or taint did.</summary>
    event EventHandler? Changed;

    string? SessionId { get; }

    string Title { get; }

    ApprovalMode? Mode { get; }

    /// <summary>Another process drives it; its mode cannot be changed from here.</summary>
    bool ReadOnly { get; }

    /// <summary>The status bar's tags for it ("tainted").</summary>
    IReadOnlyList<StatusTag> Tags { get; }

    /// <summary>Asks the engine for a mode (G-10); the answer comes back as <see cref="Mode"/>.</summary>
    void RequestMode(ApprovalMode mode);
}
