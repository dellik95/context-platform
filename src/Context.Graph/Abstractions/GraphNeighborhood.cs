using Context.Domain.Entities;

namespace Context.Graph.Abstractions;

/// <summary>
/// Node+edge neighborhood around one node - what <see cref="IGraphTraversal.NeighborhoodAsync"/>
/// returns for the Graph tab's node/edge visualization, as opposed to the flat node list
/// <see cref="IGraphTraversal.NeighborsAsync"/> returns.
/// </summary>
public sealed record GraphNeighborhood(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdgeDto> Edges);
