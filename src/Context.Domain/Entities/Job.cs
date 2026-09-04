using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class Job
{
    public Guid Id { get; set; }
    public JobType Type { get; set; }

    /// <summary>Which project this job belongs to, for Dashboard filtering. Null for host-wide
    /// maintenance jobs (<see cref="JobType.DecayMemory"/>, <see cref="JobType.BackupDb"/>) that
    /// aren't scoped to any single project.</summary>
    public Guid? ProjectId { get; set; }

    public required string PayloadJson { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int Priority { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
}
