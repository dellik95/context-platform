namespace Context.Domain.Entities;

public class Chunk
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public Guid? SymbolId { get; set; }
    public required string Text { get; set; }
    public int TokenCount { get; set; }
    public required string ContentHash { get; set; }   // key into Embedding
}
