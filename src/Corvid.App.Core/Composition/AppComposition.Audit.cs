using Corvid.App.Core.Audit;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private AuditViewModel? _audit;

    /// <summary>The Audit area: the event log, scans and the egress ledger.</summary>
    public AuditViewModel Audit => _audit ??= new AuditViewModel();
}
