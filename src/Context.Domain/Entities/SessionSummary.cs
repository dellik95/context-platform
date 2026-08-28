namespace Context.Domain.Entities;

public class SessionSummary
{
    public Guid SessionId { get; set; }                 // PK
    public string? Completed { get; set; }
    public string? Discovered { get; set; }
    public string? Decisions { get; set; }
    public string? ChangedFilesJson { get; set; }
    public string? OpenIssues { get; set; }
    public string? NextSteps { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
