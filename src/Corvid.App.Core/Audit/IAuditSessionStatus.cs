using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Audit;

/// <summary>
/// The open session's part of Audit's status bar (Audit.dc.html): its generator, decider, approval mode and tags, which
/// the page keeps beside its own detections and log size. The composition reads them from the session the window shows.
/// </summary>
public interface IAuditSessionStatus
{
    /// <summary>"claude (escalated) · cuda:0 · q0 · 410 ms"; empty when no session is open.</summary>
    string Generator { get; }

    /// <summary>"Kev-0.8B, calibrated"; empty when no session is open.</summary>
    string Decider { get; }

    /// <summary>The session's approval mode; null when no session is open.</summary>
    ApprovalMode? Mode { get; }

    /// <summary>"tainted" and the like.</summary>
    IReadOnlyList<StatusTag> Tags { get; }
}
