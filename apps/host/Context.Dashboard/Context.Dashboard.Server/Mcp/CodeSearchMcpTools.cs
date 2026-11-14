using System.ComponentModel;
using Context.Application;
using Context.Dashboard.Server.Security;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Shared;
using ModelContextProtocol.Server;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// MCP tools for hybrid context retrieval, node disclosure, code search, symbol search, and project metadata.
/// </summary>
[McpServerToolType]
public sealed class CodeSearchMcpTools(
    IContextEngine engine,
    ISearchEngine searchEngine,
    ISymbolRepository symbolRepository,
    IFileRepository fileRepository,
    CurrentProjectAccessor currentProject,
    McpNodeResolver nodeResolver,
    McpSavingsTracker savingsTracker)
{
    private const int DefaultCodeSearchLimit = 20;
    private const int DefaultSymbolSearchLimit = 20;
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

        await savingsTracker.RecordSearchSavingsAsync(
            "McpContextSearch", pack.EstimatedTokens, pack.PrimaryHits.Select(h => h.NodeId), nodeResolver.ResolveFileForNodeAsync, ct)
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
        [Description("Optional scope filters. An entry matches a file whose relative path contains it (e.g. \"src/Auth\") or whose language equals it (e.g. \"csharp\"); a file need match only one entry. Graph expansion is skipped for a scoped search.")] string[]? scope = null,
        [Description("Maximum hits to return.")] int limit = DefaultCodeSearchLimit,
        CancellationToken ct = default)
    {
        var searchQuery = new SearchQuery(currentProject.ProjectId, query, scope);
        var hits = await searchEngine.SearchAsync(searchQuery, ct).ConfigureAwait(false);
        IReadOnlyList<SearchHit> limited = [.. hits.Take(Math.Max(1, limit))];

        await savingsTracker.RecordSearchSavingsAsync(
            "McpCodeSearch", McpSavingsTracker.EstimateReturnedTokens(limited), limited.Select(h => h.NodeId), nodeResolver.ResolveFileForNodeAsync, ct)
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

        await savingsTracker.RecordSearchSavingsAsync(
            "McpSymbolSearch", McpSavingsTracker.EstimateReturnedTokens(symbols), symbols.Select(s => s.FileId), fileRepository.GetByIdAsync, ct)
            .ConfigureAwait(false);

        return symbols;
    }

    [McpServerTool(Name = "project_info")]
    [Description("Aggregate stats for a project: files indexed, symbols indexed, memory count, last-indexed time.")]
    public async Task<object> ProjectInfoAsync(CancellationToken ct = default)
        => await engine.ProjectInfoAsync(currentProject.ProjectId, ct).ConfigureAwait(false);

    private static DisclosureLevel ParseLevel(string level) =>
        Enum.TryParse<DisclosureLevel>(level, ignoreCase: true, out var parsed) ? parsed : DisclosureLevel.L1_Summary;
}
