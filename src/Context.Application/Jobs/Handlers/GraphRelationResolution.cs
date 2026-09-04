using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// By-name <see cref="Symbol"/> lookup shared between <see cref="UpdateGraphJobHandler"/>
/// (resolving a relation's cross-file target at graph-build time, for the file just indexed) and
/// <see cref="ResolveRelationsJobHandler"/> (re-attempting a previously-unresolved
/// <see cref="GraphNodeKind.UnresolvedExternal"/> placeholder once later indexing may have caught
/// up). Both need the exact same "prefer an exact-name match, else take
/// <see cref="ISymbolRepository.SearchByNameAsync"/>'s first candidate" policy and the same
/// Symbol-to-GraphNode mapping, extracted here instead of duplicated.
/// </summary>
internal static class GraphRelationResolution
{
    /// <summary>Best-effort by-name match against the project's symbols. Swallows lookup failures (best-effort by design) but not cancellation.</summary>
    public static async Task<Symbol?> ResolveByNameAsync(
        ISymbolRepository symbols, Guid projectId, string targetName, int limit, CancellationToken ct)
    {
        IReadOnlyList<Symbol> candidates;
        try
        {
            candidates = await symbols.SearchByNameAsync(projectId, targetName, limit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            candidates = [];
        }

        return candidates.FirstOrDefault(s => string.Equals(s.Name, targetName, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>Builds the GraphNode for a resolved Symbol, per the GraphNode.Id == Symbol.Id convention (see UpdateGraphJobHandler's own remarks).</summary>
    public static GraphNode ToGraphNode(Symbol symbol, Guid projectId) => new()
    {
        Id = symbol.Id,
        ProjectId = projectId,
        Kind = MapSymbolKind(symbol.Kind),
        RefId = symbol.Id,
        Label = symbol.QualifiedName,
        OwningFileId = symbol.FileId
    };

    public static GraphNodeKind MapSymbolKind(SymbolKind kind) => kind switch
    {
        SymbolKind.Class => GraphNodeKind.Class,
        SymbolKind.Interface => GraphNodeKind.Interface,
        SymbolKind.Struct => GraphNodeKind.Type,
        SymbolKind.Enum => GraphNodeKind.Type,
        SymbolKind.Function => GraphNodeKind.Function,
        SymbolKind.Method => GraphNodeKind.Method,
        SymbolKind.Property => GraphNodeKind.Variable,
        SymbolKind.Field => GraphNodeKind.Variable,
        SymbolKind.Variable => GraphNodeKind.Variable,
        SymbolKind.Module => GraphNodeKind.Module,
        SymbolKind.Type => GraphNodeKind.Type,
        _ => GraphNodeKind.Type
    };
}
