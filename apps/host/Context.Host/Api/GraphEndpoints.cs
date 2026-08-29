using Context.Application;
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
    private const int DefaultSymbolSearchLimit = 20;

    public static void MapGraphEndpoints(this WebApplication app)
    {
        app.MapGet("/api/graph/impact", ImpactAsync);
        app.MapGet("/api/symbols", SearchSymbolsAsync);
    }

    private static async Task<IResult> ImpactAsync(Guid symbolId, int? maxHops, IContextEngine engine, CancellationToken ct)
    {
        var impact = await engine.GraphImpactAsync(symbolId, maxHops ?? DefaultMaxHops, ct).ConfigureAwait(false);
        return Results.Ok(impact);
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
