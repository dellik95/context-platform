using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class MemoryConsolidationReport
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ConsolidationReportStatus Status { get; set; } = ConsolidationReportStatus.Pending;
    public required string ReportJson { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}
