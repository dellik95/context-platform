using System.ComponentModel;
using Context.Application;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Shared;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Context.Host.Mcp;

/// <summary>
/// The MCP tools named in the Context.Host task brief: <c>context_search</c>, <c>context_expand</c>,
/// <c>context_get</c>, <c>code_search</c>, <c>symbol_search</c>, <c>memory_search</c>,
/// <c>memory_get</c>, <c>graph_neighbors</c>, <c>graph_neighborhood</c>, <c>graph_path</c>,
/// <c>graph_impact</c>, <c>session_search</c>, <c>session_get</c>, <c>project_info</c>. Every tool
/// that has a direct <see cref="IContextEngine"/> method (CONTRACTS.md §10) is a thin wrapper over
/// it; the others (<c>code_search</c>, <c>symbol_search</c>, <c>memory_get</c>,
/// <c>graph_neighbors</c>, <c>graph_neighborhood</c>, <c>graph_path</c>, <c>session_search</c>,
/// <c>session_get</c>) call directly into an
/// already-registered repository/service, per the task brief's explicit allowance for tools with no
/// 1:1 engine method.
/// </summary>
/// <remarks>
/// Registered via <c>WithToolsFromAssembly()</c> in Program.cs, which — per the official MCP C#
/// SDK's own tool-hosting model — activates one instance of this class per tool call through the
/// request's DI container, so every constructor dependency below (several of them Scoped) resolves
/// safely no differently than an ASP.NET Core controller would.
/// </remarks>
[McpServerToolType]
public sealed class ContextMcpTools(
    IContextEngine engine,
    ISearchEngine searchEngine,
    ISymbolRepository symbolRepository,
    IMemoryRepository memoryRepository,
    ISessionRepository sessionRepository,
    IFileRepository fileRepository,
    IGraphTraversal graphTraversal,
    IGraphCacheProvider graphCacheProvider,
    IDbContextFactory<ContextDbContext> dbContextFactory,
    HostProjectContext hostProject)
{
    private const int DefaultCodeSearchLimit = 20;
    private const int DefaultSymbolSearchLimit = 20;
    private const int DefaultSessionSearchLimit = 20;
    private const int DefaultGraphImpactMaxHops = 3;
    private const int DefaultContextSearchBudget = 2000;

    [McpServerTool(Name = "context_search")]
    [Description("Hybrid (lexical + semantic) search over a project's code and docs, merged with ranked project memory into a token-budgeted context pack.")]
    public async Task<ContextPack> ContextSearchAsync(
        [Description("Natural-language search query.")] string query,
        [Description("Project id; defaults to this host's current project.")] Guid? projectId = null,
        [Description("Approximate token budget for the returned context pack.")] int budget = DefaultContextSearchBudget,
        CancellationToken ct = default)
    {
        var searchQuery = new SearchQuery(Resolve(projectId), query, Scope: null, TokenBudget: budget);
        return await engine.SearchContextAsync(searchQuery, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "context_expand")]
    [Description("Expands a graph node (a symbol or a file) at a chosen disclosure level.")]
    public async Task<object> ContextExpandAsync(
        [Description("Id of the symbol or file node to expand.")] Guid nodeId,
        [Description("One of L0_Metadata, L1_Summary, L2_Full. Defaults to L1_Summary.")] string level = "L1_Summary",
        CancellationToken ct = default)
        => await engine.ExpandAsync(nodeId, ParseLevel(level), ct).ConfigureAwait(false);

    [McpServerTool(Name = "context_get")]
    [Description("Convenience wrapper over context_expand that always returns the full (L2) content of a symbol or file node.")]
    public async Task<object> ContextGetAsync(
        [Description("Id of the symbol or file node to fetch in full.")] Guid nodeId,
        CancellationToken ct = default)
        => await engine.ExpandAsync(nodeId, DisclosureLevel.L2_Full, ct).ConfigureAwait(false);

    [McpServerTool(Name = "code_search")]
    [Description("Pure code/text search - raw ranked hits from the hybrid search engine, with no memory merge or context-pack budgeting.")]
    public async Task<IReadOnlyList<SearchHit>> CodeSearchAsync(
        [Description("Search query.")] string query,
        [Description("Project id; defaults to this host's current project.")] Guid? projectId = null,
        [Description("Optional path/language scope filters.")] string[]? scope = null,
        [Description("Maximum hits to return.")] int limit = DefaultCodeSearchLimit,
        CancellationToken ct = default)
    {
        var searchQuery = new SearchQuery(Resolve(projectId), query, scope);
        var hits = await searchEngine.SearchAsync(searchQuery, ct).ConfigureAwait(false);
        return [.. hits.Take(Math.Max(1, limit))];
    }

    [McpServerTool(Name = "symbol_search")]
    [Description("Finds symbols (classes, functions, methods, ...) in a project by name pattern.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.Symbol>> SymbolSearchAsync(
        [Description("Substring/pattern to match against symbol names.")] string namePattern,
        [Description("Project id; defaults to this host's current project.")] Guid? projectId = null,
        [Description("Maximum symbols to return.")] int limit = DefaultSymbolSearchLimit,
        CancellationToken ct = default)
        => await symbolRepository.SearchByNameAsync(Resolve(projectId), namePattern, Math.Max(1, limit), ct).ConfigureAwait(false);

    [McpServerTool(Name = "memory_search")]
    [Description("Searches a project's persistent memories (decisions, discoveries, bug fixes, open issues, ...).")]
    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> MemorySearchAsync(
        [Description("Search query (substring match).")] string query,
        [Description("Project id; defaults to this host's current project.")] Guid? projectId = null,
        [Description("Include memories that have been superseded by a newer one.")] bool includeSuperseded = false,
        CancellationToken ct = default)
        => await engine.SearchMemoryAsync(Resolve(projectId), query, includeSuperseded, ct).ConfigureAwait(false);

    [McpServerTool(Name = "memory_get")]
    [Description("Fetches one memory by id, or null if it doesn't exist.")]
    public async Task<Context.Domain.Entities.Memory?> MemoryGetAsync(
        [Description("Memory id.")] Guid memoryId,
        CancellationToken ct = default)
        => await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);

    [McpServerTool(Name = "graph_neighbors")]
    [Description("Lists the graph nodes reachable from a node within a number of hops, optionally filtered by edge kind.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.GraphNode>> GraphNeighborsAsync(
        [Description("Id of the graph node (symbol/file/etc.) to start from.")] Guid nodeId,
        [Description("Edge kinds to follow, e.g. [\"Calls\",\"References\"]; omit for all kinds.")] string[]? kinds = null,
        [Description("Number of hops to traverse.")] int hops = 1,
        CancellationToken ct = default)
    {
        await WarmGraphCacheForNodeAsync(nodeId, ct).ConfigureAwait(false);
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
        await WarmGraphCacheForNodeAsync(nodeId, ct).ConfigureAwait(false);
        return await graphTraversal.NeighborhoodAsync(nodeId, ParseEdgeKinds(kinds), hops, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "graph_path")]
    [Description("Finds the shortest path between two graph nodes, or null if none exists.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.GraphNode>?> GraphPathAsync(
        [Description("Starting graph node id.")] Guid fromId,
        [Description("Target graph node id.")] Guid toId,
        CancellationToken ct = default)
    {
        await WarmGraphCacheForNodeAsync(fromId, ct).ConfigureAwait(false);
        return await graphTraversal.ShortestPathAsync(fromId, toId, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "graph_impact")]
    [Description("Blast-radius analysis for a symbol: its transitive callers, implementers, and covering tests.")]
    public async Task<ImpactSet> GraphImpactAsync(
        [Description("Symbol id to analyze.")] Guid symbolId,
        [Description("Maximum hops to traverse.")] int maxHops = DefaultGraphImpactMaxHops,
        CancellationToken ct = default)
        => await engine.GraphImpactAsync(symbolId, maxHops, ct).ConfigureAwait(false);

    [McpServerTool(Name = "session_search")]
    [Description("Lists recent Claude Code sessions for a project, optionally filtered by a substring of the Claude session id.")]
    public async Task<IReadOnlyList<SessionSummaryDto>> SessionSearchAsync(
        [Description("Project id; defaults to this host's current project.")] Guid? projectId = null,
        [Description("Optional substring filter over the Claude session id.")] string? query = null,
        [Description("Maximum sessions to return.")] int limit = DefaultSessionSearchLimit,
        CancellationToken ct = default)
    {
        // No ISessionRepository.ListByProjectAsync/SearchAsync exists in CONTRACTS.md's
        // Context.Infrastructure surface (only GetByIdAsync/AddAsync/UpdateAsync, keyed by our own
        // Guid) - this queries the shared ContextDbContext directly via the pooled
        // IDbContextFactory Context.Application already registers for exactly this kind of
        // singleton-safe cross-cutting read (see its ServiceCollectionExtensions remarks). A real
        // fix would add a query method to ISessionRepository; out of scope for Context.Host.
        var pid = Resolve(projectId);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sessions = db.Sessions.Where(s => s.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(query))
        {
            sessions = sessions.Where(s => s.ClaudeSessionId != null && s.ClaudeSessionId.Contains(query));
        }

        return await sessions
            .OrderByDescending(s => s.StartedAt)
            .Take(Math.Max(1, limit))
            .Select(s => new SessionSummaryDto(
                s.Id, s.ClaudeSessionId, s.StartedAt, s.EndedAt, s.Status,
                db.SessionSummaries.Where(sm => sm.SessionId == s.Id).Select(sm => sm.Title).FirstOrDefault()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "session_get")]
    [Description("Fetches one session's metadata, event log, and summary (if it has ended and been summarized), or null if it doesn't exist.")]
    public async Task<SessionDetailDto?> SessionGetAsync(
        [Description("Session id.")] Guid sessionId,
        CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var events = await sessionRepository.GetEventsAsync(sessionId, ct).ConfigureAwait(false);
        var summary = await sessionRepository.GetSummaryAsync(sessionId, ct).ConfigureAwait(false);
        return new SessionDetailDto(session, events, summary);
    }

    [McpServerTool(Name = "project_info")]
    [Description("Aggregate stats for a project: files indexed, symbols indexed, memory count, last-indexed time.")]
    public async Task<object> ProjectInfoAsync(
        [Description("Project id; defaults to this host's current project.")] Guid? projectId = null,
        CancellationToken ct = default)
        => await engine.ProjectInfoAsync(Resolve(projectId), ct).ConfigureAwait(false);

    private Guid Resolve(Guid? projectId) => projectId ?? hostProject.ProjectId;

    private static DisclosureLevel ParseLevel(string level) =>
        Enum.TryParse<DisclosureLevel>(level, ignoreCase: true, out var parsed) ? parsed : DisclosureLevel.L1_Summary;

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

    /// <summary>
    /// Mirrors <c>ContextEngine.GraphImpactAsync</c>'s own warm-up dance (see its remarks):
    /// <see cref="IGraphTraversal"/> is keyed purely by node id and only warms a project's
    /// in-memory graph cache on the first access that project ever gets, so a cold, never-touched
    /// project needs an explicit nudge before any traversal call can see its data.
    /// </summary>
    private async Task WarmGraphCacheForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var projectId = await ResolveProjectIdForNodeAsync(nodeId, ct).ConfigureAwait(false);
        if (projectId is { } id)
        {
            await graphCacheProvider.GetOrCreateAsync(id, ct).ConfigureAwait(false);
        }
    }

    private async Task<Guid?> ResolveProjectIdForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var symbol = await symbolRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            var owningFile = await fileRepository.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
            if (owningFile is not null)
            {
                return owningFile.ProjectId;
            }
        }

        var file = await fileRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        return file?.ProjectId;
    }
}

/// <summary>Compact per-session row returned by <see cref="ContextMcpTools.SessionSearchAsync"/>.</summary>
public sealed record SessionSummaryDto(
    Guid Id, string? ClaudeSessionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, SessionStatus Status, string? Title);

/// <summary>Full session detail returned by <see cref="ContextMcpTools.SessionGetAsync"/>.</summary>
public sealed record SessionDetailDto(
    Context.Domain.Entities.Session Session,
    IReadOnlyList<Context.Domain.Entities.SessionEvent> Events,
    Context.Domain.Entities.SessionSummary? Summary);
