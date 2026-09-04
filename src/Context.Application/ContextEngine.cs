using System.Text.Json;
using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Memory.Decay;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Context.Shared;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application;

/// <summary>
/// Default <see cref="IContextEngine"/> (CONTRACTS.md §10) — the single facade every MCP tool,
/// CLI command and REST controller calls. Wires <see cref="ContextPackBuilder"/> (Context.
/// Retrieval), <see cref="IGraphTraversal"/> (Context.Graph) and the Context.Infrastructure
/// repositories together, plus the two behaviors CONTRACTS.md leaves to this layer's discretion
/// (see task brief §3): merging in ranked <see cref="Memory"/> records as historical notes/open
/// issues, and the session-scoped "already delivered" delivery cache.
/// </summary>
/// <remarks>
/// <b>Why the concrete <see cref="ContextPackBuilder"/>, not <see cref="IContextPackBuilder"/>:</b>
/// the CONTRACTS-fixed <c>IContextPackBuilder.BuildAsync(SearchQuery, CancellationToken)</c> has no
/// parameter for historical notes/open issues — Context.Retrieval's own remarks on
/// <see cref="ContextPackBuilder"/> document exactly this gap and expose a second, richer
/// <c>BuildAsync</c> overload for a caller that (like this one) has Memory data to contribute. This
/// engine depends on the concrete type specifically to reach that overload; see
/// <c>ServiceCollectionExtensions.AddContextApplication</c> for how both the interface and the
/// concrete type resolve to the same singleton instance.
/// </remarks>
public sealed class ContextEngine(
    ContextPackBuilder contextPackBuilder,
    IJobQueue jobQueue,
    IGraphTraversal graphTraversal,
    IGraphCacheProvider graphCacheProvider,
    IProjectRepository projectRepository,
    IFileRepository fileRepository,
    ISymbolRepository symbolRepository,
    IChunkRepository chunkRepository,
    IMemoryRepository memoryRepository,
    ISessionRepository sessionRepository,
    SessionDeliveryCache deliveryCache,
    IProjectSettingsService projectSettingsService) : IContextEngine
{
    private const int SummaryCharLimit = 800;

    public async Task<ContextPack> SearchContextAsync(SearchQuery query, CancellationToken ct)
    {
        var (historicalNotes, openIssues) = await BuildMemorySectionsAsync(query.ProjectId, query.Text, ct).ConfigureAwait(false);

        var pack = await contextPackBuilder.BuildAsync(query, historicalNotes, openIssues, ct).ConfigureAwait(false);

        return query.SessionId is { } sessionId ? ApplySessionDeliveryCache(pack, sessionId) : pack;
    }

    public async Task<object> ExpandAsync(Guid nodeId, DisclosureLevel level, CancellationToken ct)
    {
        var symbol = await symbolRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (symbol is not null)
        {
            return await ExpandSymbolAsync(symbol, level, ct).ConfigureAwait(false);
        }

        var file = await fileRepository.GetByIdAsync(nodeId, ct).ConfigureAwait(false);
        if (file is not null)
        {
            return await ExpandFileAsync(file, level, ct).ConfigureAwait(false);
        }

        throw new KeyNotFoundException($"No symbol or file was found for node {nodeId}.");
    }

    public async Task<ImpactSet> GraphImpactAsync(Guid symbolId, int maxHops, CancellationToken ct)
    {
        // IGraphTraversal is keyed purely by node id, with no ProjectId of its own (CONTRACTS.md
        // §4), and only warms a project's InMemoryGraphCache on the first access that project ever
        // gets (see Context.Graph.GraphCacheProvider). Resolving the owning project ourselves and
        // eagerly warming it here removes the "cold, never-touched project" limitation Context.
        // Graph's own remarks flag as a known gap, rather than leaving it as follow-up.
        var projectId = await ResolveProjectIdForNodeAsync(symbolId, ct).ConfigureAwait(false);
        if (projectId is { } id)
        {
            await graphCacheProvider.GetOrCreateAsync(id, ct).ConfigureAwait(false);
        }

        return await graphTraversal.BlastRadiusAsync(symbolId, maxHops, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct)
    {
        var results = await memoryRepository.SearchAsync(projectId, query, includeSuperseded, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        return [.. results.OrderByDescending(m => RelevanceOf(m, now))];
    }

    public async Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct)
    {
        // Claude Code fires SessionStart more than once per real session (resume, /clear,
        // /compact all re-trigger it) and Context.Dashboard.Server's in-memory ClaudeSessionMap cache (Hooks
        // layer) is lost across a restart -- either way, the same claudeSessionId can reach here
        // more than once. Reuse the existing row instead of creating a duplicate Session per
        // logical Claude Code session (claudeSessionId is stable for its whole lifetime).
        if (claudeSessionId is not null)
        {
            var existing = await sessionRepository.GetByClaudeSessionIdAsync(projectId, claudeSessionId, ct).ConfigureAwait(false);
            if (existing is not null && existing.ProjectId == projectId)
            {
                return existing.Id;
            }
        }

        var session = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ClaudeSessionId = claudeSessionId,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = null,
            Status = SessionStatus.Active
        };

        var saved = await sessionRepository.AddAsync(session, ct).ConfigureAwait(false);
        return saved.Id;
    }

    public async Task RecordEventAsync(Guid sessionId, SessionEventType type, object payload, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType());

        var sessionEvent = new SessionEvent
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Type = type,
            PayloadJson = payloadJson,
            Timestamp = DateTimeOffset.UtcNow,
            TokenEstimate = TokenEstimator.Estimate(payloadJson)
        };

        await sessionRepository.AddEventAsync(sessionEvent, ct).ConfigureAwait(false);
    }

    public async Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        var session = await sessionRepository.GetByIdAsync(projectId, sessionId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        session.Status = SessionStatus.Ended;
        session.EndedAt = DateTimeOffset.UtcNow;
        await sessionRepository.UpdateAsync(session, ct).ConfigureAwait(false);

        // SummarizeSessionJobHandler chains into ExtractMemory itself once the summary is ready -
        // see CONTRACTS.md §10's "enqueues SummarizeSession -> ExtractMemory".
        await jobQueue.EnqueueAsync(JobType.SummarizeSession, new SummarizeSessionPayload(sessionId, IsSessionEnd: true), session.ProjectId, ct: ct).ConfigureAwait(false);
    }

    public async Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        var files = await fileRepository.ListByProjectAsync(projectId, includeDeleted: false, ct).ConfigureAwait(false);

        // A single aggregate COUNT instead of an O(files) ListByFileAsync-per-file loop -- see
        // ISymbolRepository.CountByProjectAsync.
        var symbolCount = await symbolRepository.CountByProjectAsync(projectId, ct).ConfigureAwait(false);
        DateTimeOffset? lastIndexedAt = files.Count > 0 ? files.Max(f => f.UpdatedAt) : null;

        var activeMemories = await memoryRepository.ListByProjectAsync(projectId, includeSuperseded: false, ct).ConfigureAwait(false);

        return new ProjectInfo(
            project.Id,
            project.Name,
            project.RootPath,
            FilesIndexed: files.Count,
            SymbolsIndexed: symbolCount,
            MemoriesCount: activeMemories.Count,
            LastIndexedAt: lastIndexedAt);
    }

    /// <summary>
    /// Ranks the project's active memories and splits them into the two sections
    /// <see cref="ContextPackBuilder"/>'s richer overload accepts: <see cref="MemoryType.Issue"/>
    /// memories become open issues, everything else becomes a historical note. Both lists are
    /// pre-sorted, matching what <see cref="ContextPackBuilder"/> expects from a caller-supplied,
    /// already-ordered section (see its own remarks on <c>GreedyKnapsackOrdered</c>).
    /// </summary>
    /// <remarks>
    /// Ranking is normally <see cref="RelevanceOf"/> (decay + <see cref="HybridScorer.MemoryTypeBonus"/>,
    /// most-relevant-first). A <see cref="QueryIntent.IsRecall"/> query ("what did we do last
    /// time?") ranks the historical-notes section by <see cref="Context.Domain.Entities.Memory.CreatedAt"/>
    /// instead (most-recent-first) -- <see cref="ContextPackBuilder"/> has no memory timestamps to
    /// do this itself, so it's done here where the real <see cref="Context.Domain.Entities.Memory"/>
    /// records are still in hand. Open issues stay relevance-ranked either way; a "what's still
    /// pending?" query is instead handled downstream by <see cref="ContextPackBuilder"/> reordering
    /// this same relevance-ranked text by content (see <see cref="QueryIntent.IsPendingWorkFlavored"/>).
    /// </remarks>
    private async Task<(IReadOnlyList<string> HistoricalNotes, IReadOnlyList<string> OpenIssues)> BuildMemorySectionsAsync(
        Guid projectId, string queryText, CancellationToken ct)
    {
        var memories = await memoryRepository.ListByProjectAsync(projectId, includeSuperseded: false, ct).ConfigureAwait(false);
        if (memories.Count == 0)
        {
            return ([], []);
        }

        var settings = await projectSettingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        var maxMemoryNotesPerSection = Math.Clamp(settings.MaxMemoryNotesPerSection, 1, 200);

        var now = DateTimeOffset.UtcNow;
        var relevanceRanked = memories.OrderByDescending(m => RelevanceOf(m, now)).ToList();

        var notesSource = QueryIntent.IsRecall(queryText)
            ? memories.OrderByDescending(m => m.CreatedAt).ToList()
            : relevanceRanked;

        var historicalNotes = notesSource
            .Where(m => m.Type != MemoryType.Issue)
            .Take(maxMemoryNotesPerSection)
            .Select(FormatMemoryNote)
            .ToList();

        var openIssues = relevanceRanked
            .Where(m => m.Type == MemoryType.Issue)
            .Take(maxMemoryNotesPerSection)
            .Select(FormatMemoryNote)
            .ToList();

        return (historicalNotes, openIssues);
    }

    /// <summary>
    /// Base decay-based relevance (<see cref="DecayCalculator"/>, already normalized to [0, 1])
    /// plus the codemem-inspired kind bonus (<see cref="HybridScorer.MemoryTypeBonus"/>) for the
    /// memory's <see cref="MemoryType"/>, re-clamped to [0, 1] via <see cref="HybridScorer.ApplyMemoryTypeBonus"/>.
    /// </summary>
    private static double RelevanceOf(Context.Domain.Entities.Memory memory, DateTimeOffset now) =>
        HybridScorer.ApplyMemoryTypeBonus(
            DecayCalculator.ComputeRelevanceMultiplier(memory.Confidence, memory.LastReinforcedAt, memory.Pinned, now),
            memory.Type);

    private static string FormatMemoryNote(Context.Domain.Entities.Memory memory) => $"[{memory.Type}] {memory.Title}: {memory.Body}";

    /// <summary>
    /// Concrete shape of the session-scoped delivery cache described in the task brief §3: any
    /// <see cref="SearchHit"/> whose node was already delivered in full to this session within
    /// <see cref="SessionDeliveryCache.Ttl"/> gets its <see cref="SearchHit.Snippet"/> replaced by
    /// a short back-reference (id + kind + label + when) instead of the full content again — the
    /// caller (an LLM agent with a limited context window) can ask <see cref="ExpandAsync"/> for
    /// the node directly if it actually needs to see it again. <see cref="ContextPack.EstimatedTokens"/>
    /// is recomputed to reflect the swap.
    /// </summary>
    private ContextPack ApplySessionDeliveryCache(ContextPack pack, Guid sessionId)
    {
        if (pack.PrimaryHits.Count == 0)
        {
            return pack;
        }

        var newHits = new List<SearchHit>(pack.PrimaryHits.Count);
        var changed = false;

        foreach (var hit in pack.PrimaryHits)
        {
            var previousDelivery = deliveryCache.TryGetRecentDelivery(sessionId, hit.NodeId);
            if (previousDelivery is { } deliveredAt)
            {
                changed = true;
                var marker = $"[already shown to this session at {deliveredAt:O} - {hit.Kind} \"{hit.Label}\" (node {hit.NodeId}); call expand to see the full content again]";
                newHits.Add(hit with { Snippet = marker });
            }
            else
            {
                newHits.Add(hit);
            }
        }

        if (!changed)
        {
            return pack;
        }

        var estimatedTokens = TokenEstimator.Estimate(pack.ArchitectureSummary)
            + newHits.Sum(h => TokenEstimator.Estimate(h.Snippet ?? h.Label))
            + pack.HistoricalNotes.Sum(TokenEstimator.Estimate)
            + pack.OpenIssues.Sum(TokenEstimator.Estimate);

        return pack with { PrimaryHits = newHits, EstimatedTokens = estimatedTokens };
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

    private async Task<object> ExpandSymbolAsync(Symbol symbol, DisclosureLevel level, CancellationToken ct)
    {
        var file = await fileRepository.GetByIdAsync(symbol.FileId, ct).ConfigureAwait(false);

        if (level == DisclosureLevel.L0_Metadata)
        {
            return new ExpandedNode(symbol.Id, "Symbol", symbol.QualifiedName, file?.RelativePath, symbol.StartLine, symbol.EndLine, DocComment: null, Content: null);
        }

        string? content = null;
        if (file is not null)
        {
            var project = await projectRepository.GetByIdAsync(file.ProjectId, ct).ConfigureAwait(false);
            if (project is not null)
            {
                content = await TryReadSourceRangeAsync(project.RootPath, file.RelativePath, symbol.StartLine, symbol.EndLine, ct).ConfigureAwait(false);
            }
        }

        // Fall back to the chunk body text (created from exactly the same source range at index
        // time - see Indexing.IndexingPipeline) if the file isn't reachable on disk any more.
        content ??= await FindChunkTextForSymbolAsync(symbol, ct).ConfigureAwait(false);

        return new ExpandedNode(
            symbol.Id, "Symbol", symbol.QualifiedName, file?.RelativePath, symbol.StartLine, symbol.EndLine,
            symbol.DocComment,
            level == DisclosureLevel.L1_Summary ? Truncate(content, SummaryCharLimit) : content);
    }

    private async Task<object> ExpandFileAsync(DomainFile file, DisclosureLevel level, CancellationToken ct)
    {
        if (level == DisclosureLevel.L0_Metadata)
        {
            return new ExpandedNode(file.Id, "File", file.RelativePath, file.RelativePath, StartLine: null, EndLine: null, DocComment: null, Content: null);
        }

        string? content = null;
        var project = await projectRepository.GetByIdAsync(file.ProjectId, ct).ConfigureAwait(false);
        if (project is not null)
        {
            var absolutePath = Path.Combine(project.RootPath, file.RelativePath);
            if (System.IO.File.Exists(absolutePath))
            {
                content = await System.IO.File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
            }
        }

        return new ExpandedNode(
            file.Id, "File", file.RelativePath, file.RelativePath, StartLine: null, EndLine: null, DocComment: null,
            Content: level == DisclosureLevel.L1_Summary ? Truncate(content, SummaryCharLimit) : content);
    }

    private async Task<string?> FindChunkTextForSymbolAsync(Symbol symbol, CancellationToken ct)
    {
        var chunks = await chunkRepository.ListByFileAsync(symbol.FileId, ct).ConfigureAwait(false);
        return chunks.FirstOrDefault(c => c.SymbolId == symbol.Id)?.Text;
    }

    private static async Task<string?> TryReadSourceRangeAsync(string rootPath, string relativePath, int startLine, int endLine, CancellationToken ct)
    {
        var absolutePath = Path.Combine(rootPath, relativePath);
        if (!System.IO.File.Exists(absolutePath))
        {
            return null;
        }

        var lines = await System.IO.File.ReadAllLinesAsync(absolutePath, ct).ConfigureAwait(false);
        if (startLine <= 0 || endLine < startLine || startLine > lines.Length)
        {
            return null;
        }

        var from = startLine - 1;
        var to = Math.Min(lines.Length, endLine);
        return string.Join(Environment.NewLine, lines[from..to]);
    }

    private static string? Truncate(string? text, int maxChars) =>
        text is null || text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");
}

/// <summary>
/// Result of <see cref="IContextEngine.ExpandAsync"/> — the full L2 (or lighter L0/L1) content for
/// one graph node, materialized from the owning file's current source text (falling back to the
/// indexed chunk body if the file is no longer on disk).
/// </summary>
public sealed record ExpandedNode(
    Guid NodeId,
    string Kind,
    string Label,
    string? FilePath,
    int? StartLine,
    int? EndLine,
    string? DocComment,
    string? Content);

/// <summary>Result of <see cref="IContextEngine.ProjectInfoAsync"/> — aggregate project stats.</summary>
public sealed record ProjectInfo(
    Guid ProjectId,
    string Name,
    string RootPath,
    int FilesIndexed,
    int SymbolsIndexed,
    int MemoriesCount,
    DateTimeOffset? LastIndexedAt);
