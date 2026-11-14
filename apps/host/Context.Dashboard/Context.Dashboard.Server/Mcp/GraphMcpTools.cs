using System.ComponentModel;
using Context.Application;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using ModelContextProtocol.Server;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// MCP tools for graph neighborhood traversal, shortest paths, and symbol blast-radius impact analysis.
/// </summary>
[McpServerToolType]
public sealed class GraphMcpTools(
    IContextEngine engine,
    IGraphTraversal graphTraversal,
    McpNodeResolver nodeResolver)
{
    private const int DefaultGraphImpactMaxHops = 3;

    [McpServerTool(Name = "graph_neighbors")]
    [Description("Lists the graph nodes reachable from a node within a number of hops, optionally filtered by edge kind.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.GraphNode>> GraphNeighborsAsync(
        [Description("Id of the graph node (symbol/file/etc.) to start from.")] Guid nodeId,
        [Description("Edge kinds to follow, e.g. [\"Calls\",\"References\"]; omit for all kinds.")] string[]? kinds = null,
        [Description("Number of hops to traverse.")] int hops = 1,
        CancellationToken ct = default)
    {
        await nodeResolver.WarmGraphCacheForNodeAsync(nodeId, ct).ConfigureAwait(false);
        return await graphTraversal.NeighborsAsync(nodeId, ParseEdgeKinds(kinds), hops, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "graph_neighborhood")]
    [Description("Like graph_neighbors, but also returns the edges connecting the neighborhood - the node+edge shape a graph visualization needs.")]
    public async Task<GraphNeighborhood> GraphNeighborhoodAsync(
        [Description("Id of the graph node (symbol/file/etc.) to start from.")] Guid nodeId,
        [Description("Edge kinds to follow, e.g. [\"Calls\",\"References\"]; omit for all kinds.")] string[]? kinds = null,
        [Description("Number of hops to traverse.")] int hops = 1,
        CancellationToken ct = default)
    {
        await nodeResolver.WarmGraphCacheForNodeAsync(nodeId, ct).ConfigureAwait(false);
        return await graphTraversal.NeighborhoodAsync(nodeId, ParseEdgeKinds(kinds), hops, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "graph_path")]
    [Description("Finds the shortest path between two graph nodes, or null if none exists.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.GraphNode>?> GraphPathAsync(
        [Description("Starting graph node id.")] Guid fromId,
        [Description("Target graph node id.")] Guid toId,
        CancellationToken ct = default)
    {
        await nodeResolver.WarmGraphCacheForNodeAsync(fromId, ct).ConfigureAwait(false);
        return await graphTraversal.ShortestPathAsync(fromId, toId, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "graph_impact")]
    [Description("Blast-radius analysis for a symbol: its transitive callers, implementers, and covering tests.")]
    public async Task<ImpactSet> GraphImpactAsync(
        [Description("Symbol id to analyze.")] Guid symbolId,
        [Description("Maximum hops to traverse.")] int maxHops = DefaultGraphImpactMaxHops,
        CancellationToken ct = default)
        => await engine.GraphImpactAsync(symbolId, maxHops, ct).ConfigureAwait(false);

    private static GraphEdgeKind[]? ParseEdgeKinds(string[]? kinds)
    {
        if (kinds is null || kinds.Length == 0)
        {
            return null;
        }

        var parsed = kinds
            .Select(k => Enum.TryParse<GraphEdgeKind>(k, ignoreCase: true, out var kind) ? (GraphEdgeKind?)kind : null)
            .Where(k => k.HasValue)
            .Select(k => k!.Value)
            .ToArray();

        return parsed.Length == 0 ? null : parsed;
    }
}
