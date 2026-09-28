namespace Context.Domain.Enums;

/// <summary>Pending (a stored plan awaiting review) moves to Applying when an apply is claimed, then to
/// Applied or Failed; Discarded only from Pending.</summary>
public enum ConsolidationReportStatus
{
    Pending = 0,
    Applied = 1,
    Discarded = 2,
    Applying = 3,
    Failed = 4,
}
