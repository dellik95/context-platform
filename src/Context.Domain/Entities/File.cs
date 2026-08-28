namespace Context.Domain.Entities;

public class File
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string RelativePath { get; set; }
    public string? Language { get; set; }
    public required string ContentHash { get; set; }   // SHA-256 hex
    public long SizeBytes { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
