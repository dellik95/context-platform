namespace Context.Domain.Entities;

public class FileVersion
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset IndexedAt { get; set; }
    public int AstVersion { get; set; }
}
