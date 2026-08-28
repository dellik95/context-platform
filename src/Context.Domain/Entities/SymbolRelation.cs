using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class SymbolRelation
{
    public Guid Id { get; set; }
    public Guid SourceSymbolId { get; set; }
    public Guid TargetSymbolId { get; set; }
    public RelationKind Kind { get; set; }
    public bool IsResolved { get; set; }               // false = syntactic guess, true = resolution-pass confirmed
}
