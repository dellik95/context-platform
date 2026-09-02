using Context.Application;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Host.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Host.Api;

public sealed record SymbolSummaryDto(Guid Id, string Name, string QualifiedName, string Kind, Guid FileId, int StartLine, int EndLine)
{
    public static SymbolSummaryDto FromEntity(Context.Domain.Entities.Symbol symbol) =>
        new(symbol.Id, symbol.Name, symbol.QualifiedName, symbol.Kind.ToString(), symbol.FileId, symbol.StartLine, symbol.EndLine);
}

/// <summary>
/// <c>/api/graph/impact</c> and <c>/api/symbols</c> — the latter exists purely so the Dashboard's
/// Graph panel can resolve a human-typed name to the <see cref="Context.Domain.Entities.Symbol.Id"/>
/// <c>/api/graph/impact</c> actually needs (CONTRACTS.md's IContextEngine facade has no
/// name-search method of its own — this goes straight to <see cref="ISymbolRepository"/>, same as
/// Context.Cli's own symbol/graph command does).
/// </summary>
public static class GraphEndpoints
{
    private const int DefaultMaxHops = 3;
    private const int DefaultNeighborhoodHops = 1;
    private const int DefaultSymbolSearchLimit = 20;

    public static void MapGraphEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireBearerToken();
        group.MapGet("/api/graph/impact", ImpactAsync);
        group.MapGet("/api/graph/neighborhood", NeighborhoodAsync);
        group.MapGet("/api/graph/full", FullGraphAsync);
        group.MapGet("/api/symbols", SearchSymbolsAsync);
    }

    private static async Task<IResult> ImpactAsync(Guid symbolId, int? maxHops, IContextEngine engine, CancellationToken ct)
    {
        var impact = await engine.GraphImpactAsync(symbolId, maxHops ?? DefaultMaxHops, ct).ConfigureAwait(false);
        return Results.Ok(impact);
    }

    private static async Task<IResult> NeighborhoodAsync(
        Guid nodeId, int? hops, string[]? kinds,
        IGraphTraversal graphTraversal, IGraphCacheProvider graphCacheProvider,
        ISymbolRepository symbols, IFileRepository files, CancellationToken ct)
    {
        // IGraphTraversal is keyed purely by node id with no ProjectId of its own, and only warms a
        // project's InMemoryGraphCache on the first access that project ever gets (see Context.
        // Graph.GraphCacheProvider) - mirrors ContextEngine.GraphImpactAsync's/ContextMcpTools'
        // WarmGraphCacheForNodeAsync dance so a cold, never-touched project's graph is found too.
        var projectId = await ResolveProjectIdForNodeAsync(nodeId, symbols, files, ct).ConfigureAwait(false);
        if (projectId is { } id)
        {
            await graphCacheProvider.GetOrCreateAsync(id, ct).ConfigureAwait(false);
        }

        var neighborhood = await graphTraversal.NeighborhoodAsync(
            nodeId, ParseEdgeKinds(kinds), hops ?? DefaultNeighborhoodHops, ct).ConfigureAwait(false);
        return Results.Ok(neighborhood);
    }

    private static async Task<IResult> FullGraphAsync(
        Guid projectId, string[]? kinds, IGraphTraversal graphTraversal, CancellationToken ct)
    {
        var fullGraph = await graphTraversal.FullGraphAsync(projectId, ParseEdgeKinds(kinds), ct).ConfigureAwait(false);
        return Results.Ok(fullGraph);
    }

    private static async Task<Guid?> ResolveProjectIdForNodeAsync(
        Guid nodeId, ISymbolRepository symbols, IFileRepository files, CancellationToken ct)
    {
        var symbol = await symbols.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            var owningFile = await files.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
            if (owningFile is not null)
            {
                return owningFile.ProjectId;
            }
        }

        var file = await files.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        return file?.ProjectId;
    }

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

    private static async Task<IResult> SearchSymbolsAsync(
        Guid projectId, string q, int? limit, ISymbolRepository symbols, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Ok(Array.Empty<SymbolSummaryDto>());
        }

        var results = await symbols.SearchByNameAsync(projectId, q, limit ?? DefaultSymbolSearchLimit, ct).ConfigureAwait(false);
        return Results.Ok(results.Select(SymbolSummaryDto.FromEntity));
    }
}
