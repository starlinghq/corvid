using Corvid.App.Core.Audit;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IAuditService? _auditService;
    private AuditViewModel? _audit;

    /// <summary>
    /// The Audit area's service: the event log, the egress ledger, scans and trace export. Demo mode serves Audit.png's
    /// moment on the frozen clock; engine mode gets it from <see cref="Services"/>.
    /// </summary>
    public IAuditService AuditService => _auditService ??= Services.CreateAuditService(this);

    /// <summary>The Audit area: the event log, scans and the egress ledger. Links from Sessions land through the shell.</summary>
    public AuditViewModel Audit => _audit ??= new AuditViewModel(AuditService, Time, Shell, new HubAuditSessionStatus(SessionHub));

    /// <summary>Audit's view of the open session's status, read from the session hub when Audit writes its status bar.</summary>
    private sealed class HubAuditSessionStatus(SessionHub hub) : IAuditSessionStatus
    {
        public string Generator => hub.Current?.StatusGenerator ?? "";

        public string Decider => hub.Current?.StatusDecider ?? "";

        public ApprovalMode? Mode => hub.Current?.Mode;

        public IReadOnlyList<StatusTag> Tags => hub.Current?.StatusTags ?? [];
    }
}
