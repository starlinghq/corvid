using Corvid.App.Core.Audit;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IAuditService? _auditService;
    private AuditViewModel? _audit;

    /// <summary>
    /// The Audit area's service: the event log, the egress ledger, scans and trace export. Demo mode (the only mode until
    /// Corvid.App.Engine exists) serves Audit.png's moment on the frozen clock.
    /// </summary>
    public IAuditService AuditService => _auditService ??= new DemoAuditService(Time);

    /// <summary>The Audit area: the event log, scans and the egress ledger. Links from Sessions land through the shell.</summary>
    public AuditViewModel Audit => _audit ??= new AuditViewModel(AuditService, Time, Shell);
}
