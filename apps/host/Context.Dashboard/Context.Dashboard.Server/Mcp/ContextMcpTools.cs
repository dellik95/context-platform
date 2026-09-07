using System.ComponentModel;
using System.Security;
using Context.Application;
using Context.Application.Settings;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Dashboard.Server.Security;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// The MCP tools named in the Context.Dashboard.Server task brief: <c>context_search</c>, <c>context_expand</c>,
/// <c>context_get</c>, <c>code_search</c>, <c>symbol_search</c>, <c>memory_search</c>,
/// <c>memory_get</c>, <c>memory_remember</c>, <c>graph_neighbors</c>, <c>graph_neighborhood</c>,
/// <c>graph_path</c>, <c>graph_impact</c>, <c>session_search</c>, <c>session_get</c>,
/// <c>project_info</c>. Every tool that has a direct <see cref="IContextEngine"/> method
/// (CONTRACTS.md §10) is a thin wrapper over it; the others (<c>code_search</c>,
/// <c>symbol_search</c>, <c>memory_get</c>, <c>graph_neighbors</c>, <c>graph_neighborhood</c>,
/// <c>graph_path</c>, <c>session_search</c>, <c>session_get</c>) call directly into an
/// already-registered repository/service, per the task brief's explicit allowance for tools with no
/// 1:1 engine method. <c>memory_remember</c> (docs/architecture/MEMORY_PIPELINE_PLAN.md §F) is the
/// one exception with its own gating check (<c>ProjectSettingsDto.ExplicitRememberEnabled</c>)
/// before it delegates to <see cref="IContextEngine.RememberExplicitlyAsync"/>.
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
    CurrentProjectAccessor currentProject,
    IUsageRecorder usageRecorder,
    IProjectSettingsService projectSettingsService,
    ILogger<ContextMcpTools>? logger = null)
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
        [Description("Approximate token budget for the returned context pack.")] int budget = DefaultContextSearchBudget,
        CancellationToken ct = default)
    {
        var searchQuery = new SearchQuery(currentProject.ProjectId, query, Scope: null, TokenBudget: budget);
        var pack = await engine.SearchContextAsync(searchQuery, ct).ConfigureAwait(false);

        // pack.EstimatedTokens is already the same TokenEstimator-based figure computed over
        // exactly this pack's content (see ContextEngine), so it doubles as "returnedTokens" here
        // with no separate estimate needed.
        await RecordSearchSavingsAsync(
            "McpContextSearch", pack.EstimatedTokens, pack.PrimaryHits.Select(h => h.NodeId), ResolveFileForNodeAsync, ct)
            .ConfigureAwait(false);

        return pack;
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
        [Description("Optional path/language scope filters.")] string[]? scope = null,
        [Description("Maximum hits to return.")] int limit = DefaultCodeSearchLimit,
        CancellationToken ct = default)
    {
        var searchQuery = new SearchQuery(currentProject.ProjectId, query, scope);
        var hits = await searchEngine.SearchAsync(searchQuery, ct).ConfigureAwait(false);
        IReadOnlyList<SearchHit> limited = [.. hits.Take(Math.Max(1, limit))];

        await RecordSearchSavingsAsync(
            "McpCodeSearch", EstimateReturnedTokens(limited), limited.Select(h => h.NodeId), ResolveFileForNodeAsync, ct)
            .ConfigureAwait(false);

        return limited;
    }

    [McpServerTool(Name = "symbol_search")]
    [Description("Finds symbols (classes, functions, methods, ...) in a project by name pattern.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.Symbol>> SymbolSearchAsync(
        [Description("Substring/pattern to match against symbol names.")] string namePattern,
        [Description("Maximum symbols to return.")] int limit = DefaultSymbolSearchLimit,
        CancellationToken ct = default)
    {
        var symbols = await symbolRepository.SearchByNameAsync(currentProject.ProjectId, namePattern, Math.Max(1, limit), ct).ConfigureAwait(false);

        await RecordSearchSavingsAsync(
            "McpSymbolSearch", EstimateReturnedTokens(symbols), symbols.Select(s => s.FileId), fileRepository.GetByIdAsync, ct)
            .ConfigureAwait(false);

        return symbols;
    }

    [McpServerTool(Name = "memory_search")]
    [Description("Searches a project's persistent memories (decisions, discoveries, bug fixes, open issues, ...). Each memory carries a short reusable Topic tag (e.g. \"Auth middleware\") naming the feature/subsystem it's about, so memories on the same subject from different sessions can be found together via the topic filter.")]
    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> MemorySearchAsync(
        [Description("Search query (substring match).")] string query,
        [Description("Include memories that have been superseded by a newer one.")] bool includeSuperseded = false,
        [Description("Optional exact-match filter on the memory's Topic tag, e.g. \"Auth middleware\".")] string? topic = null,
        CancellationToken ct = default)
        => await engine.SearchMemoryAsync(currentProject.ProjectId, query, includeSuperseded, ct, topic).ConfigureAwait(false);

    [McpServerTool(Name = "memory_get")]
    [Description("Fetches one memory by id, or null if it doesn't exist.")]
    public async Task<Context.Domain.Entities.Memory?> MemoryGetAsync(
        [Description("Memory id.")] Guid memoryId,
        CancellationToken ct = default)
        => await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);

    [McpServerTool(Name = "memory_remember")]
    [Description("Explicitly stores something the user just asked to be remembered, bypassing the normal " +
        "N-event/session-end extraction triggers. Call this the moment a user says things like 'remember " +
        "that...', 'note for later...', or gives a standing instruction — do not wait for enough events to " +
        "accumulate. You already have the full context, so fill in facts/topic/concepts/files yourself " +
        "rather than leaving this an unstructured narrative dump — the same structured shape the automatic " +
        "session-summary extraction produces, so this memory is just as useful when it resurfaces later.")]
    public async Task<Context.Domain.Entities.Memory> MemoryRememberAsync(
        [Description("The memory's main content: 2-5 self-contained sentences naming the SPECIFIC files (by path), " +
            "classes/functions/symbols (by name), and config keys actually involved — never a generic stand-in " +
            "('a service', 'some files'). State the actual mechanism/reasoning, not just that something happened.")] string content,
        [Description("One of Decision, Discovery, BugFix, Feature, Refactor, Change, Exploration, Architecture, Issue, Instruction. Defaults to Instruction.")] string? type = null,
        [Description("Short one-line title (under 100 chars), outcome-focused (what changed/was learned, not what you did to find out). " +
            "Omit to auto-derive one from the first sentence of content.")] string? title = null,
        [Description("Short 2-5 word phrase naming the feature/subsystem/component this memory is about (e.g. \"Auth middleware\", " +
            "\"Job dispatcher retry logic\") — reuse an existing name for that area rather than inventing a one-off phrase, so memories " +
            "about the same subject from different sessions share it and can be found together via memory_search's topic filter.")] string? topic = null,
        [Description("2-5 short, atomic, standalone facts pulled out of content — each must make sense read on its own with no " +
            "surrounding narrative (shown when a search result needs to say something useful in very few tokens). Reuse concrete " +
            "names from content (a file path, a symbol, a number, a config key) rather than a vaguer paraphrase.")] string[]? facts = null,
        [Description("1-3 tags naming what KIND of knowledge this is, chosen ONLY from this fixed list: how-it-works, why-it-exists, " +
            "what-changed, problem-solution, gotcha, pattern, trade-off. Omit if none clearly apply.")] string[]? concepts = null,
        [Description("Specific files this memory is about that were read/examined but not changed. Omit if not applicable.")] string[]? filesRead = null,
        [Description("Specific files this memory is about that were actually edited because of it. Omit if not applicable.")] string[]? filesModified = null,
        CancellationToken ct = default)
    {
        var settings = await projectSettingsService.GetAsync(currentProject.ProjectId, ct).ConfigureAwait(false);
        if (!settings.ExplicitRememberEnabled)
        {
            logger?.LogInformation(
                "memory_remember was called for project {ProjectId} but ExplicitRememberEnabled is false; rejecting.",
                currentProject.ProjectId);
            throw new InvalidOperationException(
                "memory_remember is disabled for this project (ExplicitRememberEnabled is false in project settings).");
        }

        return await engine.RememberExplicitlyAsync(
            currentProject.ProjectId, content, ParseMemoryType(type), ct,
            title, topic, facts, concepts, filesRead, filesModified).ConfigureAwait(false);
    }

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
    [Description("Lists recent Claude Code sessions for a project, optionally filtered by a substring of the Claude session id. " +
        "Excludes sessions with nothing worth surfacing: a session that ended but never got a summary generated for it, and a " +
        "session that consists only of tool calls with no actual user prompt in it.")]
    public async Task<IReadOnlyList<SessionSummaryDto>> SessionSearchAsync(
        [Description("Optional substring filter over the Claude session id.")] string? query = null,
        [Description("Maximum sessions to return.")] int limit = DefaultSessionSearchLimit,
        CancellationToken ct = default)
    {
        // No ISessionRepository.ListByProjectAsync/SearchAsync exists in CONTRACTS.md's
        // Context.Infrastructure surface (only GetByIdAsync/AddAsync/UpdateAsync, keyed by our own
        // Guid) - this queries the shared ContextDbContext directly via the pooled
        // IDbContextFactory Context.Application already registers for exactly this kind of
        // singleton-safe cross-cutting read (see its ServiceCollectionExtensions remarks). A real
        // fix would add a query method to ISessionRepository; out of scope for Context.Dashboard.Server.
        var pid = currentProject.ProjectId;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sessions = db.Sessions.Where(s => s.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(query))
        {
            sessions = sessions.Where(s => s.ClaudeSessionId != null && s.ClaudeSessionId.Contains(query));
        }

        // A still-Active session naturally has no SessionSummary yet (that only runs on
        // session-end) so it stays included; an Ended one with none means summarization either
        // never ran or produced nothing -- there's no title/content for this list to show, so drop it.
        sessions = sessions.Where(s =>
            s.Status != SessionStatus.Ended || db.SessionSummaries.Any(sm => sm.SessionId == s.Id));

        // A session with zero Prompt events is pure tool-call/automation noise -- nothing the user
        // actually asked for -- and clutters this list with entries that have nothing to search for.
        sessions = sessions.Where(s =>
            db.SessionEvents.Any(e => e.SessionId == s.Id && e.Type == SessionEventType.Prompt));

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
        var pid = currentProject.ProjectId;
        var session = await sessionRepository.GetByIdAsync(pid, sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var events = await sessionRepository.GetEventsAsync(pid, sessionId, ct).ConfigureAwait(false);
        var summary = await sessionRepository.GetSummaryAsync(pid, sessionId, ct).ConfigureAwait(false);
        var chunks = await sessionRepository.GetSummaryChunksAsync(pid, sessionId, ct).ConfigureAwait(false);
        return new SessionDetailDto(session, events, summary, chunks);
    }

    [McpServerTool(Name = "project_info")]
    [Description("Aggregate stats for a project: files indexed, symbols indexed, memory count, last-indexed time.")]
    public async Task<object> ProjectInfoAsync(CancellationToken ct = default)
        => await engine.ProjectInfoAsync(currentProject.ProjectId, ct).ConfigureAwait(false);

    private static DisclosureLevel ParseLevel(string level) =>
        Enum.TryParse<DisclosureLevel>(level, ignoreCase: true, out var parsed) ? parsed : DisclosureLevel.L1_Summary;

    /// <summary>
    /// Case-insensitive parse of a <see cref="MemoryType"/> name for the agent-facing
    /// <c>memory_remember</c> tool - a bad/unrecognized string from the caller must fail soft to
    /// the default rather than throw (an agent passing a slightly-wrong type name shouldn't 500).
    /// </summary>
    private MemoryType ParseMemoryType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return MemoryType.Instruction;
        }

        if (Enum.TryParse<MemoryType>(type, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        logger?.LogInformation("memory_remember received an unrecognized MemoryType {Type}; defaulting to Instruction.", type);
        return MemoryType.Instruction;
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
        var file = await ResolveFileForNodeAsync(nodeId, ct).ConfigureAwait(false);
        return file?.ProjectId;
    }

    /// <summary>
    /// Resolves a <see cref="SearchHit.NodeId"/> (a Symbol id, a File id, or occasionally a Chunk id
    /// that neither repository recognizes - see <see cref="SearchHit"/>'s remarks) to the file it
    /// belongs to: first as a symbol's owning file, falling back to treating the id as a file id
    /// directly. Returns null for a node this can't map to a file (e.g. a Chunk id), which is fine
    /// for both of this method's callers - <see cref="WarmGraphCacheForNodeAsync"/> just skips the
    /// warm-up, and the MCP token-savings accounting just doesn't count that hit's file size.
    /// </summary>
    private async Task<Context.Domain.Entities.File?> ResolveFileForNodeAsync(Guid nodeId, CancellationToken ct)
    {
        var symbol = await symbolRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            var owningFile = await fileRepository.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);
            if (owningFile is not null)
            {
                return owningFile;
            }
        }

        return await fileRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
    }

    private static int EstimateReturnedTokens(IEnumerable<SearchHit> hits) =>
        hits.Sum(h => TokenEstimator.Estimate(h.Label) + TokenEstimator.Estimate(h.Snippet ?? string.Empty));

    private static int EstimateReturnedTokens(IEnumerable<Context.Domain.Entities.Symbol> symbols) =>
        symbols.Sum(s => TokenEstimator.Estimate(s.QualifiedName) + TokenEstimator.Estimate(s.DocComment ?? string.Empty));

    /// <summary>
    /// Records the estimated token savings (a companion, counterfactual metric to the real LLM-spend
    /// rows <see cref="Context.Memory.Summarization.LlmSessionSummarizer"/>/
    /// <see cref="Context.Memory.Extraction.LlmMemoryExtractor"/> record - see
    /// <see cref="Context.Domain.Entities.UsageRecord.SavedTokens"/> for the exact methodology) of one
    /// search-style MCP tool call: <paramref name="returnedTokens"/> is what the call actually sent
    /// back (already estimated by the caller with the same <see cref="TokenEstimator"/> heuristic
    /// used everywhere else in the platform); <paramref name="ids"/> + <paramref name="resolveFile"/>
    /// identify the distinct files behind the results, whose sizes stand in for what reading them in
    /// full would have cost. Best-effort: recording a savings estimate must never break the tool
    /// call that already produced its real result, so every exception (other than cancellation) here
    /// is logged and swallowed.
    /// </summary>
    private async Task RecordSearchSavingsAsync(
        string purpose,
        int returnedTokens,
        IEnumerable<Guid> ids,
        Func<Guid, CancellationToken, Task<Context.Domain.Entities.File?>> resolveFile,
        CancellationToken ct)
    {
        try
        {
            var files = new Dictionary<Guid, Context.Domain.Entities.File>();
            foreach (var id in ids.Distinct())
            {
                var file = await resolveFile(id, ct).ConfigureAwait(false);
                if (file is not null)
                {
                    files[file.Id] = file;
                }
            }

            if (files.Count == 0)
            {
                return;
            }

            var fullFileTokens = files.Values.Sum(EstimateFileTokens);
            var savedTokens = Math.Max(0, fullFileTokens - returnedTokens);

            await usageRecorder.RecordAsync(new Context.Domain.Entities.UsageRecord
            {
                ProjectId = currentProject.ProjectId,
                Purpose = purpose,
                Provider = string.Empty,
                SavedTokens = savedTokens,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to record MCP search-tool token savings for {Purpose}.", purpose);
        }
    }

    /// <summary>
    /// Estimates a file's full-read token cost from its indexed <see cref="Context.Domain.Entities.File.SizeBytes"/>
    /// without reading it, falling back to an on-disk stat (against the project's RootPath +
    /// RelativePath) for the rare file whose indexed size isn't known - so a read/permission error
    /// on that fallback (the file was moved/deleted since indexing, etc.) just prices it at zero
    /// rather than failing the whole savings estimate.
    /// </summary>
    private int EstimateFileTokens(Context.Domain.Entities.File file)
    {
        if (file.SizeBytes > 0)
        {
            return TokenEstimator.EstimateFromByteLength(file.SizeBytes);
        }

        try
        {
            var fullPath = Path.Combine(currentProject.Project.RootPath, file.RelativePath);
            return TokenEstimator.EstimateFromByteLength(new FileInfo(fullPath).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return 0;
        }
    }
}

/// <summary>Compact per-session row returned by <see cref="ContextMcpTools.SessionSearchAsync"/>.</summary>
public sealed record SessionSummaryDto(
    Guid Id, string? ClaudeSessionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, SessionStatus Status, string? Title);

/// <summary>Full session detail returned by <see cref="ContextMcpTools.SessionGetAsync"/>.</summary>
public sealed record SessionDetailDto(
    Context.Domain.Entities.Session Session,
    IReadOnlyList<Context.Domain.Entities.SessionEvent> Events,
    Context.Domain.Entities.SessionSummary? Summary,
    IReadOnlyList<Context.Domain.Entities.SessionSummaryChunk> Chunks);
