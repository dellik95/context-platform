using Context.Domain.Entities;

namespace Context.Graph.Abstractions;

/// <summary>One node reached while walking a blast-radius query, with how it was reached.</summary>
public record ImpactNode(GraphNode Node, int Hops, string Path);
