namespace Context.Indexing.Abstractions;

/// <summary>
/// A best-effort, syntactic (unresolved) relation between a symbol and a name it references.
/// <see cref="TargetName"/> is a bare identifier — resolving it to a concrete symbol/graph
/// node across files is a later pipeline stage (see <c>SymbolRelation.IsResolved</c> in
/// CONTRACTS.md §1). See CONTRACTS.md §3.
/// </summary>
public record RelationDraft(
    string SourceQualifiedName,
    string TargetName,
    Context.Domain.Enums.RelationKind Kind);
