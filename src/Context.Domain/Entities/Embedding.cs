namespace Context.Domain.Entities;

public class Embedding
{
    public required string ContentHash { get; set; }   // PK
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public int Dim { get; set; }
    public required byte[] Vector { get; set; }         // packed float32[]
    public DateTimeOffset CreatedAt { get; set; }
}
