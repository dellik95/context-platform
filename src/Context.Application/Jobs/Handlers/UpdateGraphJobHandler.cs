using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Shared;
using Microsoft.Extensions.Logging;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Rebuilds (or invalidates) the graph nodes/edges owned by one file, from its current Symbols.
/// </summary>
/// <remarks>
/// <para>
/// <b>Id unification:</b> this handler deliberately sets <c>GraphNode.Id = Symbol.Id</c> (and
/// <c>= File.Id</c> for the file's own node), rather than minting an unrelated synthetic id and
/// only recording the domain id via <c>RefId</c>. <c>IGraphTraversal</c>'s contract methods are
/// keyed purely by <c>GraphNode.Id</c> (CONTRACTS.md §4), and Context.Retrieval's
/// <c>HybridSearchEngine</c> already documents, as a known limitation, that graph expansion finds
/// nothing when a candidate's Symbol/File id and its GraphNode's synthetic id are different
/// values. Unifying the two id spaces here removes that limitation outright instead of leaving it
/// as follow-up work, and is safe because nothing else depends on <c>GraphNode.Id</c> being
/// independent from <c>RefId</c>.
/// </para>
/// <para>
/// <b>Relation re-parsing:</b> <see cref="Domain.Entities.SymbolRelation"/> rows are never
/// written (Context.Infrastructure exposes no repository for that entity — only
/// <c>ISymbolRepository</c> — and <c>RelationDraft</c>s are otherwise a transient, in-pipeline
/// value the indexing pass doesn't persist). So this handler recovers relations by re-reading the
/// file from disk and re-running the matching <c>ILanguageParser</c>'s
/// <c>ExtractRelations</c> — a deliberate simplification/duplication of work already done once
/// during indexing, flagged as follow-up (passing serialized <c>RelationDraft</c>s through the
/// job payload would avoid the second parse).
/// </para>
/// <para>
/// Cross-file relation targets are resolved best-effort against <c>ISymbolRepository</c> by name;
/// anything that still doesn't resolve becomes (or reuses) a deterministic
/// <see cref="GraphNodeKind.UnresolvedExternal"/> node, per <c>SymbolRelation.IsResolved</c>'s
/// documented meaning in CONTRACTS.md §1 ("syntactic guess" vs "resolution-pass confirmed") — full
/// cross-file resolution remains a later pipeline stage, exactly as CONTRACTS.md's
/// <c>RelationDraft</c> remarks call out.
/// </para>
/// </remarks>
public sealed class UpdateGraphJobHandler(
    IProjectRepository projects,
    IFileRepository files,
    ISymbolRepository symbols,
    IEnumerable<ILanguageParser> parsers,
    IGraphStore graphStore,
    ILogger<UpdateGraphJobHandler>? logger = null) : IJobHandler
{
    public JobType Type => JobType.UpdateGraph;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<UpdateGraphPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(UpdateGraphPayload)}.");

        if (payload.Invalidate)
        {
            await graphStore.InvalidateByOwningFileAsync(payload.ProjectId, payload.FileId, ct).ConfigureAwait(false);
            return;
        }

        var file = await files.GetByIdAsync(payload.FileId, ct).ConfigureAwait(false);
        if (file is null || file.IsDeleted)
        {
            // Deleted again before this job ran - nothing to rebuild, just make sure it's gone.
            await graphStore.InvalidateByOwningFileAsync(payload.ProjectId, payload.FileId, ct).ConfigureAwait(false);
            return;
        }

        var currentSymbols = await symbols.ListByFileAsync(file.Id, ct).ConfigureAwait(false);

        var nodes = new List<GraphNode>
        {
            new()
            {
                Id = file.Id,
                ProjectId = payload.ProjectId,
                Kind = GraphNodeKind.File,
                RefId = file.Id,
                Label = file.RelativePath,
                OwningFileId = file.Id
            }
        };

        foreach (var symbol in currentSymbols)
        {
            nodes.Add(new GraphNode
            {
                Id = symbol.Id,
                ProjectId = payload.ProjectId,
                Kind = MapSymbolKind(symbol.Kind),
                RefId = symbol.Id,
                Label = symbol.QualifiedName,
                OwningFileId = file.Id
            });
        }

        var nodesById = nodes.ToDictionary(n => n.Id);
        var edges = new List<GraphEdge>();

        // Containment: File -> top-level symbol, parent symbol -> nested symbol.
        foreach (var symbol in currentSymbols)
        {
            var containerId = symbol.ParentSymbolId is { } parentId && nodesById.ContainsKey(parentId)
                ? parentId
                : file.Id;

            edges.Add(NewEdge(payload.ProjectId, containerId, symbol.Id, GraphEdgeKind.Contains, file.Id));
        }

        await AddRelationEdgesAsync(payload.ProjectId, file, currentSymbols, nodes, nodesById, edges, ct).ConfigureAwait(false);

        await graphStore.UpsertAsync(nodes, edges, ct).ConfigureAwait(false);
    }

    private async Task AddRelationEdgesAsync(
        Guid projectId, DomainFile file, IReadOnlyList<Symbol> currentSymbols,
        List<GraphNode> nodes, Dictionary<Guid, GraphNode> nodesById, List<GraphEdge> edges, CancellationToken ct)
    {
        IReadOnlyList<RelationDraft> relationDrafts;
        try
        {
            var project = await projects.GetByIdAsync(projectId, ct).ConfigureAwait(false);
            if (project is null)
            {
                return;
            }

            var extension = Path.GetExtension(file.RelativePath);
            var parser = parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));
            if (parser is null)
            {
                return;
            }

            var absolutePath = Path.Combine(project.RootPath, file.RelativePath);
            if (!System.IO.File.Exists(absolutePath))
            {
                return;
            }

            var sourceText = await System.IO.File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
            var ast = parser.Parse(sourceText);
            var projectSymbolNames = await symbols.GetAllNamesAsync(projectId, ct).ConfigureAwait(false);
            relationDrafts = parser.ExtractRelations(ast, projectSymbolNames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Relation extraction is a best-effort enrichment on top of the containment structure
            // already built above; a re-parse failure must not fail the whole graph update.
            logger?.LogWarning(ex, "Failed to re-derive relations for file {FileId} ({RelativePath}); keeping containment edges only.", file.Id, file.RelativePath);
            return;
        }

        if (relationDrafts.Count == 0)
        {
            return;
        }

        var symbolIdByQualifiedName = currentSymbols.ToDictionary(s => s.QualifiedName, s => s.Id, StringComparer.Ordinal);
        var seenEdgeKeys = new HashSet<(Guid Source, Guid Target, GraphEdgeKind Kind)>();

        foreach (var relation in relationDrafts)
        {
            ct.ThrowIfCancellationRequested();

            if (!symbolIdByQualifiedName.TryGetValue(relation.SourceQualifiedName, out var sourceId))
            {
                continue; // source must be one of the symbols this file just yielded
            }

            var targetId = await ResolveTargetAsync(projectId, relation.TargetName, currentSymbols, nodes, nodesById, ct).ConfigureAwait(false);
            if (targetId is null)
            {
                continue;
            }

            var edgeKind = MapRelationKind(relation.Kind);
            if (!seenEdgeKeys.Add((sourceId, targetId.Value, edgeKind)))
            {
                continue; // de-dup: the heuristic parser can report the same relation more than once
            }

            edges.Add(NewEdge(projectId, sourceId, targetId.Value, edgeKind, file.Id));
        }
    }

    private async Task<Guid?> ResolveTargetAsync(
        Guid projectId, string targetName, IReadOnlyList<Symbol> currentSymbols,
        List<GraphNode> nodes, Dictionary<Guid, GraphNode> nodesById, CancellationToken ct)
    {
        var local = currentSymbols.FirstOrDefault(s =>
            string.Equals(s.Name, targetName, StringComparison.Ordinal) ||
            string.Equals(s.QualifiedName, targetName, StringComparison.Ordinal) ||
            s.QualifiedName.EndsWith("." + targetName, StringComparison.Ordinal));

        if (local is not null)
        {
            return local.Id;
        }

        IReadOnlyList<Symbol> candidates;
        try
        {
            candidates = await symbols.SearchByNameAsync(projectId, targetName, limit: 5, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            candidates = [];
        }

        var resolved = candidates.FirstOrDefault(s => string.Equals(s.Name, targetName, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault();

        if (resolved is not null)
        {
            if (!nodesById.ContainsKey(resolved.Id))
            {
                var node = new GraphNode
                {
                    Id = resolved.Id,
                    ProjectId = projectId,
                    Kind = MapSymbolKind(resolved.Kind),
                    RefId = resolved.Id,
                    Label = resolved.QualifiedName,
                    OwningFileId = resolved.FileId
                };
                nodes.Add(node);
                nodesById[node.Id] = node;
            }

            return resolved.Id;
        }

        // Still unresolved: a stable synthetic placeholder, keyed deterministically so repeated
        // UpdateGraph runs for different files referencing the same unresolved name converge on
        // the same node instead of duplicating it.
        var syntheticId = DeterministicGuid($"unresolved:{projectId}:{targetName}");
        if (!nodesById.ContainsKey(syntheticId))
        {
            var node = new GraphNode
            {
                Id = syntheticId,
                ProjectId = projectId,
                Kind = GraphNodeKind.UnresolvedExternal,
                RefId = null,
                Label = targetName,
                OwningFileId = null
            };
            nodes.Add(node);
            nodesById[node.Id] = node;
        }

        return syntheticId;
    }

    private static GraphEdge NewEdge(Guid projectId, Guid sourceId, Guid targetId, GraphEdgeKind kind, Guid owningFileId) => new()
    {
        // Deterministic id so re-running this handler for the same (source,target,kind) updates
        // the existing edge in place (GraphRepository.UpsertAsync matches by Id) instead of
        // accumulating duplicates.
        Id = DeterministicGuid($"edge:{sourceId}:{targetId}:{kind}"),
        ProjectId = projectId,
        SourceNodeId = sourceId,
        TargetNodeId = targetId,
        Kind = kind,
        OwningFileId = owningFileId,
        Weight = 1.0
    };

    private static Guid DeterministicGuid(string key) => Guid.ParseExact(Hashing.HashText(key)[..32], "N");

    private static GraphNodeKind MapSymbolKind(SymbolKind kind) => kind switch
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

    private static GraphEdgeKind MapRelationKind(RelationKind kind) => kind switch
    {
        RelationKind.Calls => GraphEdgeKind.Calls,
        RelationKind.References => GraphEdgeKind.References,
        RelationKind.Extends => GraphEdgeKind.Extends,
        RelationKind.Implements => GraphEdgeKind.Implements,
        RelationKind.Uses => GraphEdgeKind.Uses,
        RelationKind.Imports => GraphEdgeKind.Imports,
        RelationKind.Exports => GraphEdgeKind.Exports,
        RelationKind.Tests => GraphEdgeKind.Tests,
        RelationKind.DependsOn => GraphEdgeKind.DependsOn,
        _ => GraphEdgeKind.References
    };
}
