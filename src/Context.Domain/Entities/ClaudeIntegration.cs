namespace Context.Domain.Entities;

public class ClaudeIntegration
{
    public Guid ProjectId { get; set; }                 // PK
    public required string HookVersion { get; set; }
    public required string McpConfigHash { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public string? BackupPath { get; set; }
}
