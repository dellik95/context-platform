namespace Context.Domain.Entities;

/// <summary>A normalized "kind:value" key (file, symbol, package, ...) a memory mentions.</summary>
public class MemoryEntity
{
    public Guid ProjectId { get; set; }
    public Guid MemoryId { get; set; }
    public required string Key { get; set; }
    public required string Display { get; set; }
}
