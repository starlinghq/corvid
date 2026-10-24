namespace Corvid.App.Core.Audit;

/// <summary>The Audit area (docs/plan/ui.md §4.12).</summary>
public sealed class AuditViewModel() : AreaViewModel(Area.Audit, "The event log per session, scans with the detection rules, trace export and the egress ledger.");
