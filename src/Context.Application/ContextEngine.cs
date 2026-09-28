using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Decay;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Context.Shared;
using Microsoft.Extensions.AI;
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
    IProjectSettingsService projectSettingsService,
    IMemoryStore memoryStore,
    IMemoryConsolidator memoryConsolidator,
    IMemoryVectorIndex memoryVectorIndex,
    ISessionSummaryVectorIndex sessionSummaryVectorIndex,
    IProviderResolver providerResolver) : IContextEngine
{
    private const int SummaryCharLimit = 800;
    private const int ExplicitRememberTitleCharLimit = 80;
    // The user explicitly asked for this to be remembered - high confidence by construction,
    // not LLM-judged (see docs/architecture/MEMORY_PIPELINE_PLAN.md §F).
    private const double ExplicitRememberConfidence = 0.95;

    // memory_search previously ranked purely by decay+pinned+type bonus (RelevanceOf) -- a
    // substring match only decided *inclusion*, never order, so the query text had zero influence
    // on ranking. SemanticMatchWeight folds in real query relevance (lexical substring hit, or
    // semantic cosine similarity from IMemoryVectorIndex) while still letting decay/pinned/type
    // bonus break ties and demote stale matches, same spirit as HybridScorer for code search but
    // with its own weight -- HybridScorer's weights are benchmark-tuned for code chunks
    // (RetrievalQualityHarnessTests), not memory search, so they aren't reused here.
    private const double SemanticMatchWeight = 0.6;
    private const int SemanticSearchLimit = 50;

    // Same pattern as EmbeddingLookupAdapter.InteractiveOptions / MemoryConsolidator.InteractiveOptions:
    // flags a live memory_search query so OllamaEmbeddingGeneratorDecorator skips the concurrency
    // gate it applies to background EmbedChunk/ExtractMemory jobs.
    private static readonly EmbeddingGenerationOptions InteractiveEmbeddingOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

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

    /// <summary>
    /// Hybrid lexical + semantic memory search: <see cref="IMemoryRepository.SearchAsync"/>'s
    /// substring match still decides which memories are eligible (cheap, exact, always available
    /// offline), while <see cref="IMemoryVectorIndex"/> now also contributes semantically similar
    /// memories the substring pass would miss entirely (paraphrase, synonym, no shared words).
    /// Ranking blends both into <see cref="SemanticMatchWeight"/> vs. the decay/pinned/type-bonus
    /// <see cref="RelevanceOf"/> score (see that constant's remarks).
    /// </summary>
    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null)
    {
        var lexicalHits = await memoryRepository.SearchAsync(projectId, query, includeSuperseded, ct, topic).ConfigureAwait(false);
        var lexicalIds = lexicalHits.Select(m => m.Id).ToHashSet();

        var semanticScores = await SemanticSearchAsync(projectId, query, topic, ct).ConfigureAwait(false);

        var byId = lexicalHits.ToDictionary(m => m.Id);
        var missingIds = semanticScores.Keys.Where(id => !byId.ContainsKey(id)).ToList();
        if (missingIds.Count > 0)
        {
            var fetched = await memoryRepository.GetByIdsAsync(missingIds, ct).ConfigureAwait(false);
            foreach (var memory in fetched)
            {
                // The vector index now filters by topic too (see SqliteMemoryVectorIndex.SearchAsync),
                // but still only by ProjectId/Topic -- includeSuperseded still needs enforcing here
                // so a semantic-only hit can't bypass it.
                if (!includeSuperseded && memory.SupersededById is not null)
                {
                    continue;
                }

                byId[memory.Id] = memory;
            }
        }

        if (byId.Count == 0)
        {
            return [];
        }

        var normalizedSemantic = NormalizeSemanticScores(semanticScores);
        var now = DateTimeOffset.UtcNow;

        return [.. byId.Values.OrderByDescending(m => CombinedScore(m, lexicalIds, normalizedSemantic, now))];
    }

    private async Task<Dictionary<Guid, double>> SemanticSearchAsync(Guid projectId, string query, string? topic, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var embeddingResult = await embeddingGenerator.GenerateAsync(query, InteractiveEmbeddingOptions, ct).ConfigureAwait(false);

        var hits = await memoryVectorIndex.SearchAsync(
            projectId, embeddingGenerator.GetProviderModelKey(), embeddingResult.Vector.ToArray(), SemanticSearchLimit, ct, topic)
            .ConfigureAwait(false);

        return hits.ToDictionary(h => h.MemoryId, h => h.Score);
    }

    private static IReadOnlyDictionary<Guid, double> NormalizeSemanticScores(Dictionary<Guid, double> raw)
    {
        if (raw.Count == 0)
        {
            return raw;
        }

        var ids = raw.Keys.ToList();
        var normalized = HybridScorer.NormalizeMinMax([.. ids.Select(id => raw[id])]);

        var result = new Dictionary<Guid, double>(raw.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            result[ids[i]] = normalized[i];
        }

        return result;
    }

    /// <summary>A lexical substring hit counts as a maximal match (1.0) regardless of its
    /// (unrelated) semantic score — a direct text match is a strong signal on its own, and
    /// shouldn't be buried under a merely-decent semantic-only hit. Semantic-only hits use their
    /// min-max-normalized cosine similarity.</summary>
    private static double CombinedScore(
        Context.Domain.Entities.Memory memory, HashSet<Guid> lexicalIds, IReadOnlyDictionary<Guid, double> normalizedSemantic, DateTimeOffset now)
    {
        var semanticScore = normalizedSemantic.TryGetValue(memory.Id, out var s) ? s : 0.0;
        var matchScore = lexicalIds.Contains(memory.Id) ? Math.Max(semanticScore, 1.0) : semanticScore;

        return (SemanticMatchWeight * matchScore) + ((1 - SemanticMatchWeight) * RelevanceOf(memory, now));
    }

    /// <summary>
    /// Hybrid lexical + semantic search over <see cref="SessionSummaryChunk"/>s, mirroring
    /// <see cref="SearchMemoryAsync"/> exactly (see its own remarks) — a lexical substring hit still
    /// counts as a maximal match, a semantic-only hit uses its min-max-normalized cosine similarity.
    /// Unlike memories, chunks have no decay/pinned/type-bonus concept to blend in as a tie-breaker,
    /// so the match score alone decides ranking.
    /// </summary>
    public async Task<IReadOnlyList<SessionSummaryChunk>> SearchSessionSummariesAsync(
        Guid projectId, string query, CancellationToken ct, Guid? sessionId = null, int limit = 20)
    {
        var lexicalHits = await sessionRepository.SearchSummaryChunksAsync(projectId, query, limit, ct, sessionId).ConfigureAwait(false);
        var lexicalIds = lexicalHits.Select(c => c.Id).ToHashSet();

        var semanticScores = await SemanticSearchSessionSummariesAsync(projectId, query, sessionId, ct).ConfigureAwait(false);

        var byId = lexicalHits.ToDictionary(c => c.Id);
        var missingIds = semanticScores.Keys.Where(id => !byId.ContainsKey(id)).ToList();
        foreach (var id in missingIds)
        {
            // Fetched one at a time via the existing single-chunk lookup rather than adding a new
            // batched repository method - a semantic-only hit that isn't also a lexical hit is the
            // uncommon case, and SemanticSearchLimit already caps how many of these there can be.
            var chunk = await sessionRepository.GetSummaryChunkByIdAsync(projectId, id, ct).ConfigureAwait(false);
            if (chunk is not null && (sessionId is null || chunk.SessionId == sessionId))
            {
                byId[chunk.Id] = chunk;
            }
        }

        if (byId.Count == 0)
        {
            return [];
        }

        var normalizedSemantic = NormalizeSemanticScores(semanticScores);

        return [.. byId.Values.OrderByDescending(c => CombinedChunkScore(c, lexicalIds, normalizedSemantic)).Take(limit)];
    }

    private async Task<Dictionary<Guid, double>> SemanticSearchSessionSummariesAsync(Guid projectId, string query, Guid? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var embeddingResult = await embeddingGenerator.GenerateAsync(query, InteractiveEmbeddingOptions, ct).ConfigureAwait(false);

        var hits = await sessionSummaryVectorIndex.SearchAsync(
            projectId, embeddingGenerator.GetProviderModelKey(), embeddingResult.Vector.ToArray(), SemanticSearchLimit, ct, sessionId)
            .ConfigureAwait(false);

        return hits.ToDictionary(h => h.ChunkId, h => h.Score);
    }

    /// <summary>Same "lexical hit is a maximal match" rule as <see cref="CombinedScore"/>, without a
    /// decay/type-bonus term to blend in — chunks have no analogous concept.</summary>
    private static double CombinedChunkScore(SessionSummaryChunk chunk, HashSet<Guid> lexicalIds, IReadOnlyDictionary<Guid, double> normalizedSemantic)
    {
        var semanticScore = normalizedSemantic.TryGetValue(chunk.Id, out var s) ? s : 0.0;
        return lexicalIds.Contains(chunk.Id) ? Math.Max(semanticScore, 1.0) : semanticScore;
    }

    public async Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct, string? cwd = null)
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
                // Until StaleSessionScheduler existed, only the SessionEnd hook itself ever set
                // Ended, so a reused row was always still Active. Now a background timeout can end
                // a session that later turns out to still be alive (hook traffic resumes after a
                // network blip, e.g.) -- treat renewed hook traffic as proof of that and un-end it,
                // rather than silently handing new events to a row stuck in Ended forever.
                if (existing.Status == SessionStatus.Ended)
                {
                    existing.Status = SessionStatus.Active;
                    existing.EndedAt = null;
                    await sessionRepository.UpdateAsync(existing, ct).ConfigureAwait(false);
                }

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
            Status = SessionStatus.Active,
            Cwd = cwd
        };

        var saved = await sessionRepository.AddAsync(session, ct).ConfigureAwait(false);
        return saved.Id;
    }

    public async Task RecordEventAsync(Guid projectId, Guid sessionId, SessionEventType type, object payload, CancellationToken ct)
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

        var settings = await projectSettingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        await EnqueueExtractionBatchAsync(projectId, sessionId, Math.Max(1, settings.MinEventsForMemoryExtraction), ct).ConfigureAwait(false);
    }

    private async Task EnqueueExtractionBatchAsync(Guid projectId, Guid sessionId, int minEvents, CancellationToken ct)
    {
        var batchId = await sessionRepository.ClaimEventsForExtractionAsync(sessionId, minEvents, ct).ConfigureAwait(false);
        if (batchId is null)
        {
            return;
        }

        await jobQueue.EnqueueAsync(
            JobType.ExtractMemory, new ExtractMemoryPayload(sessionId, EventBatchId: batchId), projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    public async Task ExtractMemoryFromCompactSummaryAsync(Guid projectId, Guid sessionId, string compactSummary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(compactSummary))
        {
            return;
        }

        await jobQueue.EnqueueAsync(
            JobType.ExtractMemory, new ExtractMemoryPayload(sessionId, CompactSummary: compactSummary), projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    public async Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct, string? reason = null)
    {
        var session = await sessionRepository.GetByIdAsync(projectId, sessionId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        session.Status = SessionStatus.Ended;
        session.EndedAt = DateTimeOffset.UtcNow;
        session.EndReason = reason;
        await sessionRepository.UpdateAsync(session, ct).ConfigureAwait(false);

        // No later event will push the remainder over the batch threshold, so flush it now.
        await EnqueueExtractionBatchAsync(session.ProjectId, sessionId, minEvents: 1, ct).ConfigureAwait(false);
        await jobQueue.EnqueueAsync(JobType.SummarizeSession, new SummarizeSessionPayload(sessionId, IsSessionEnd: true), session.ProjectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    public async Task RequestSessionSummaryAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        var session = await sessionRepository.GetByIdAsync(projectId, sessionId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        await jobQueue.EnqueueAsync(JobType.SummarizeSession, new SummarizeSessionPayload(sessionId, IsSessionEnd: false), session.ProjectId, JobPriority.Session, ct).ConfigureAwait(false);
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
    /// See <see cref="IContextEngine.RememberExplicitlyAsync"/>. Builds a single
    /// <see cref="MemoryCandidate"/> directly from <paramref name="content"/> and the caller's own
    /// structured fields (no LLM candidate generation - the caller already has the context) and
    /// hands it straight to <see cref="IMemoryConsolidator.ConsolidateAsync"/>, the same decision
    /// logic <c>LlmMemoryExtractor</c> uses for the periodic extraction path - zero dependency on
    /// event counts, <see cref="Context.Domain.Entities.SessionSummaryChunk"/> state, or a session
    /// at all (<c>sessionId: null</c>, since an MCP-originated remember has no session in scope).
    /// </summary>
    public async Task<Context.Domain.Entities.Memory> RememberExplicitlyAsync(
        Guid projectId,
        string content,
        MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var candidate = new MemoryCandidate(
            Type: type ?? MemoryType.Instruction,
            Title: string.IsNullOrWhiteSpace(title) ? BuildExplicitRememberTitle(content) : title.Trim(),
            Body: content,
            Confidence: ExplicitRememberConfidence,
            Facts: facts ?? [],
            Concepts: concepts ?? [],
            FilesRead: filesRead ?? [],
            FilesModified: filesModified ?? [],
            Topic: string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
            ScopePath: Context.Memory.Extraction.ScopePathHeuristics.DeriveFromFiles(filesModified ?? [], filesRead ?? []));

        var existing = (await memoryStore.GetExistingWithEmbeddingsAsync(projectId, ct).ConfigureAwait(false)).ToList();
        var result = await memoryConsolidator.ConsolidateAsync(projectId, sessionId: null, candidate, existing, isInteractive: true, ct).ConfigureAwait(false);
        return result.Memory;
    }

    /// <summary>First sentence of <paramref name="content"/>, or its first ~<see cref="ExplicitRememberTitleCharLimit"/>
    /// characters when no sentence-ending punctuation appears early enough - same spirit as the
    /// TITLE guidance <c>MemoryExtractionPromptBuilder</c> gives the LLM for the periodic path.</summary>
    private static string BuildExplicitRememberTitle(string content)
    {
        var trimmed = content.Trim();
        var sentenceEnd = trimmed.IndexOfAny(['.', '!', '?']);
        if (sentenceEnd > 0 && sentenceEnd < ExplicitRememberTitleCharLimit)
        {
            return trimmed[..(sentenceEnd + 1)];
        }

        return trimmed.Length <= ExplicitRememberTitleCharLimit
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, ExplicitRememberTitleCharLimit), "…");
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

    /// <summary>
    /// Prefixes the memory's <see cref="Context.Domain.Entities.Memory.Topic"/> in its own bracket
    /// when set, e.g. "[Decision][Auth middleware] ..." — otherwise unchanged from the pre-Topic
    /// format so existing notes without a topic (and any consumer/test asserting on that exact
    /// string) are unaffected.
    /// </summary>
    private static string FormatMemoryNote(Context.Domain.Entities.Memory memory) =>
        string.IsNullOrWhiteSpace(memory.Topic)
            ? $"[{memory.Type}] {memory.Title}: {memory.Body}"
            : $"[{memory.Type}][{memory.Topic}] {memory.Title}: {memory.Body}";

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
