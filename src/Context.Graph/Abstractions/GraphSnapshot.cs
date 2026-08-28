using Context.Domain.Entities;

namespace Context.Graph.Abstractions;

/// <summary>Full node/edge set for one project, as returned by <see cref="IGraphPersistence.GetAllAsync"/>.</summary>
public record GraphSnapshot(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
