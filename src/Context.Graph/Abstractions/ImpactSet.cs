namespace Context.Graph.Abstractions;

/// <summary>Result of <see cref="IGraphTraversal.BlastRadiusAsync"/> ("graph_impact" feature).</summary>
public record ImpactSet(
    IReadOnlyList<ImpactNode> Callers,
    IReadOnlyList<ImpactNode> Implementers,
    IReadOnlyList<ImpactNode> CoveringTests);
