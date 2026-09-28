namespace Context.Domain.Entities;

public class MemoryEmbedding
{
    public Guid MemoryId { get; set; }                  // PK
    public required byte[] Vector { get; set; }
    public int Dim { get; set; }
    // Embedding provider/model key the vector was produced with; null means unknown and stale.
    public string? ModelKey { get; set; }
}
