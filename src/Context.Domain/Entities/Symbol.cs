using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class Symbol
{
    public Guid Id { get; set; }
    public Guid FileId { get; set; }
    public Guid? ParentSymbolId { get; set; }
    public SymbolKind Kind { get; set; }
    public required string Name { get; set; }
    public required string QualifiedName { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public required string SignatureHash { get; set; }
    public required string BodyHash { get; set; }
    public string? DocComment { get; set; }
}
