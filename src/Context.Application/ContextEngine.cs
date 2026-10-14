using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Decay;
using Context.Memory.Extraction;
using Context.Memory.Search;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Context.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application;

/// <summary>
/// Default <see cref="IContextEngine"/>: the single facade every MCP tool, CLI command and REST
/// controller calls. Wires <see cref="ContextPackBuilder"/> (Context.Retrieval),
/// <see cref="IGraphTraversal"/> (Context.Graph) and the Context.Infrastructure repositories
/// together, merges ranked memories into packs as historical notes/open issues, and owns the
/// session-scoped "already delivered" delivery cache.
/// </summary>
/// <remarks>
/// <b>Why the concrete <see cref="ContextPackBuilder"/>, not <see cref="IContextPackBuilder"/>:</b>
/// <c>IContextPackBuilder.BuildAsync(SearchQuery, CancellationToken)</c> has no
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
    IMemoryConsolidator memoryConsolidator,
    IMemoryStore memoryStore,
    ISessionSummaryVectorIndex sessionSummaryVectorIndex,
    IProviderResolver providerResolver,
    ILogger<ContextEngine>? logger = null) : IContextEngine
{
    private const int SummaryCharLimit = 800;
    private const int ExplicitRememberTitleCharLimit = 80;

    private const int SemanticSearchLimit = 50;
    private const int MaxMemoryNotesPerSectionCap = 200;

    // Calibrated on qwen3-embedding:4b against a live project's memories: paraphrased questions scored
    // 0.54-0.80 against the memory answering them, unrelated questions at most 0.46 against any memory.
    private const double MemorySemanticMinSimilarity = 0.50;
    private const double MemoryLexicalBoost = 0.10;
    private const double MemoryEntityBoost = 0.10;
    private const double MemoryRelevanceTieBreak = 0.03;
    private const double MemoryTypeTieBreak = 0.02;

    // Same pattern as EmbeddingLookupAdapter.InteractiveOptions / MemoryConsolidator.InteractiveOptions:
    // flags a live memory_search query so OllamaEmbeddingGeneratorDecorator skips the concurrency
    // gate it applies to background EmbedChunk/ExtractMemory jobs.
    private static readonly EmbeddingGenerationOptions InteractiveEmbeddingOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

    public async Task<ContextPack> SearchContextAsync(SearchQuery query, CancellationToken ct)
    {
        var (historicalNotes, openIssues) = await BuildMemorySectionsAsync(query, ct).ConfigureAwait(false);

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
        // IGraphTraversal is keyed purely by node id, with no ProjectId of its own, and only warms a project's InMemoryGraphCache on the first access that project ever
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
    /// Semantic memory search. For a query naming a subject, a memory qualifies by embedding similarity of
    /// at least <see cref="MemorySemanticMinSimilarity"/>, an exact entity match or containing every query
    /// term, and ranks by similarity with lexical and entity matches as boosts; without usable embeddings
    /// the match is lexical. Relevance and type only break ties, and alone order a query naming no subject.
    /// </summary>
    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(
        Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null)
    {
        var memories = await memoryRepository.ListByProjectAsync(projectId, includeSuperseded, ct).ConfigureAwait(false);
        var topicFilter = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        var pool = memories
            .Where(m => topicFilter is null || string.Equals(m.Topic?.Trim(), topicFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pool.Count == 0)
        {
            return [];
        }

        var parsed = MemoryQuery.Parse(query);
        var matches = await MatchAsync(projectId, parsed, pool, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        return [.. pool
            .Where(m => !parsed.HasSubject || matches.ContainsKey(m.Id))
            .OrderByDescending(m => Score(m, matches.GetValueOrDefault(m.Id), now))];
    }

    /// <summary>Match score of each qualifying memory of <paramref name="pool"/>; empty when the query names no subject.</summary>
    private async Task<Dictionary<Guid, double>> MatchAsync(
        Guid projectId, MemoryQuery query, IReadOnlyList<Context.Domain.Entities.Memory> pool, CancellationToken ct)
    {
        var matches = new Dictionary<Guid, double>();
        if (!query.HasSubject)
        {
            return matches;
        }

        var entities = await EntityMatchesAsync(projectId, query, ct).ConfigureAwait(false);
        var similarities = await SemanticSimilaritiesAsync(projectId, query.Text, pool, ct).ConfigureAwait(false);
        foreach (var memory in pool)
        {
            var lexical = query.LexicalMatch(memory);
            var entity = entities.GetValueOrDefault(memory.Id);
            if (similarities is null)
            {
                if (lexical > 0 || entity > 0)
                {
                    matches[memory.Id] = Math.Max(lexical, entity);
                }

                continue;
            }

            var similarity = similarities.GetValueOrDefault(memory.Id);
            if (similarity >= MemorySemanticMinSimilarity || entity > 0 || lexical >= 1)
            {
                matches[memory.Id] = similarity + (MemoryLexicalBoost * lexical) + (MemoryEntityBoost * entity);
            }
        }

        return matches;
    }

    /// <summary>Share of the query's strong entity keys each live memory carries.</summary>
    private async Task<Dictionary<Guid, double>> EntityMatchesAsync(Guid projectId, MemoryQuery query, CancellationToken ct)
    {
        if (query.EntityKeys.Count == 0)
        {
            return [];
        }

        var entities = await memoryRepository.ListLiveEntitiesByKeysAsync(projectId, [.. query.EntityKeys], ct).ConfigureAwait(false);
        return entities
            .GroupBy(e => e.MemoryId)
            .ToDictionary(g => g.Key, g => (double)g.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() / query.EntityKeys.Count);
    }

    /// <summary>
    /// Cosine similarity of the query to every memory of <paramref name="pool"/> whose stored embedding
    /// comes from the current model, or null when similarity cannot rank the pool: a no-op or failing
    /// embedding provider, or no memory embedded by the current model yet.
    /// </summary>
    private async Task<Dictionary<Guid, double>?> SemanticSimilaritiesAsync(
        Guid projectId, string query, IReadOnlyList<Context.Domain.Entities.Memory> pool, CancellationToken ct)
    {
        IEmbeddingGenerator<string, Embedding<float>> generator;
        float[] queryVector;
        try
        {
            generator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
            if (generator.IsNoOp())
            {
                return null;
            }

            queryVector = (await generator.GenerateAsync(query, InteractiveEmbeddingOptions, ct).ConfigureAwait(false)).Vector.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Embedding a memory query failed for project {ProjectId}; memory search falls back to lexical matching.", projectId);
            return null;
        }

        var modelKey = generator.GetProviderModelKey();
        var embeddings = await memoryStore.GetEmbeddingsAsync([.. pool.Select(m => m.Id)], ct).ConfigureAwait(false);
        var similarities = embeddings
            .Where(e => e.Value.ModelKey == modelKey)
            .ToDictionary(e => e.Key, e => CosineSimilarity.Compute(queryVector, e.Value.Vector));
        if (similarities.Count == 0)
        {
            logger?.LogWarning(
                "No memory of project {ProjectId} is embedded by {ModelKey}; memory search falls back to lexical matching until embeddings are recreated.",
                projectId, modelKey);
            return null;
        }

        return similarities;
    }

    private static double Score(Context.Domain.Entities.Memory memory, double match, DateTimeOffset now) =>
        match
        + (MemoryRelevanceTieBreak * DecayCalculator.ComputeRelevanceMultiplier(memory.Confidence, memory.LastReinforcedAt, memory.Pinned, memory.Tier, now))
        + (MemoryTypeTieBreak * HybridScorer.NormalizedMemoryTypeBonus(memory.Type));

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

    public async Task<MemoryDetail?> GetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct)
    {
        var memory = await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != projectId)
        {
            return null;
        }

        var evidence = await memoryRepository.ListEvidenceAsync([memoryId], ct).ConfigureAwait(false);
        var entities = await memoryRepository.ListEntitiesAsync([memoryId], ct).ConfigureAwait(false);
        return new MemoryDetail(memory, evidence, entities);
    }

    public async Task<bool> ForgetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct)
    {
        var memory = await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != projectId)
        {
            return false;
        }

        return await MemoryContests.ForgetAsync(memoryStore, memoryId, ct).ConfigureAwait(false);
    }

    public async Task<Guid?> RequestForgetAsync(Guid projectId, Guid memoryId, CancellationToken ct)
    {
        var memory = await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != projectId)
        {
            return null;
        }

        return await jobQueue.EnqueueAsync(
            JobType.ForgetMemory, new ForgetMemoryPayload(memoryId), projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hybrid lexical + semantic search over <see cref="SessionSummaryChunk"/>s: a lexical substring
    /// hit counts as a maximal match, a semantic-only hit uses its min-max-normalized cosine
    /// similarity, and the match score alone decides ranking.
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
        var candidate = BuildExplicitCandidate(content, type, title, topic, facts, concepts, filesRead, filesModified);

        var result = await memoryConsolidator.ConsolidateAsync(projectId, sessionId: null, candidate, isInteractive: true, ct).ConfigureAwait(false);
        return result.Memory;
    }

    public async Task<Guid> RequestRememberAsync(
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
        // Building the candidate rejects blank content, the only way an explicit candidate fails validation.
        BuildExplicitCandidate(content, type, title, topic, facts, concepts, filesRead, filesModified);

        var payload = new RememberMemoryPayload(content, type, title, topic, facts, concepts, filesRead, filesModified);
        return await jobQueue.EnqueueAsync(JobType.RememberMemory, payload, projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    private static MemoryCandidate BuildExplicitCandidate(
        string content,
        MemoryType? type,
        string? title,
        string? topic,
        IReadOnlyList<string>? facts,
        IReadOnlyList<string>? concepts,
        IReadOnlyList<string>? filesRead,
        IReadOnlyList<string>? filesModified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        return new MemoryCandidate(
            Type: type ?? MemoryType.Fact,
            Title: string.IsNullOrWhiteSpace(title) ? BuildExplicitRememberTitle(content) : title.Trim(),
            Body: content,
            Source: EvidenceSource.Explicit,
            Facts: facts ?? [],
            Concepts: concepts ?? [],
            FilesRead: filesRead ?? [],
            FilesModified: filesModified ?? [],
            Topic: string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
            ScopePath: Context.Memory.Extraction.ScopePathHeuristics.DeriveFromFiles(filesModified ?? [], filesRead ?? []));
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
    /// Query-aware memory sections for a pack, ranked like <see cref="SearchMemoryAsync"/>. Historical
    /// notes hold matching non-issue memories (Active before Contested); open issues hold matching
    /// issues, or every live issue for a pending-work query. A query naming no subject gets the top
    /// memories by relevance.
    /// </summary>
    private async Task<(IReadOnlyList<string> HistoricalNotes, IReadOnlyList<string> OpenIssues)> BuildMemorySectionsAsync(
        SearchQuery query, CancellationToken ct)
    {
        var memories = await memoryRepository.ListByProjectAsync(query.ProjectId, includeSuperseded: false, ct).ConfigureAwait(false);
        if (memories.Count == 0)
        {
            return ([], []);
        }

        var settings = await projectSettingsService.GetAsync(query.ProjectId, ct).ConfigureAwait(false);
        var maxNotes = Math.Clamp(settings.MaxMemoryNotesPerSection, 1, MaxMemoryNotesPerSectionCap);

        var parsed = MemoryQuery.Parse(query.Text);
        var matches = await MatchAsync(query.ProjectId, parsed, memories, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        bool IsCandidate(Context.Domain.Entities.Memory m) => !parsed.HasSubject || matches.ContainsKey(m.Id);
        double ScoreOf(Context.Domain.Entities.Memory m) => Score(m, matches.GetValueOrDefault(m.Id), now);

        var knowledge = memories
            .Where(m => m.Type != MemoryType.Issue && IsCandidate(m))
            .OrderBy(m => m.Status == MemoryStatus.Contested)
            .ThenByDescending(ScoreOf)
            .Take(maxNotes);

        var includeAllIssues = !parsed.HasSubject || QueryIntent.IsPendingWork(parsed.Text);
        var issues = memories
            .Where(m => m.Type == MemoryType.Issue && (includeAllIssues || matches.ContainsKey(m.Id)))
            .OrderByDescending(ScoreOf)
            .Take(maxNotes);

        return (
            RenderNotes([.. knowledge], (int)(query.TokenBudget * ContextPackBuilder.DefaultHistoricalReserveRatio)),
            RenderNotes([.. issues], (int)(query.TokenBudget * ContextPackBuilder.DefaultOpenIssuesReserveRatio)));
    }

    /// <summary>Canonical notes, with facts appended in order while the section budget still has room
    /// after every plain note <see cref="ContextPackBuilder"/> would keep.</summary>
    private static List<string> RenderNotes(IReadOnlyList<Context.Domain.Entities.Memory> memories, int budget)
    {
        var notes = memories.Select(m => MemoryNote.Format(m)).ToList();

        var kept = new List<int>();
        var used = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            var cost = TokenEstimator.Estimate(notes[i]);
            if (used + cost <= budget)
            {
                kept.Add(i);
                used += cost;
            }
        }

        var spare = budget - used;
        foreach (var i in kept)
        {
            var withFacts = MemoryNote.Format(memories[i], includeFacts: true);
            var extra = TokenEstimator.Estimate(withFacts) - TokenEstimator.Estimate(notes[i]);
            if (withFacts.Length > notes[i].Length && extra <= spare)
            {
                notes[i] = withFacts;
                spare -= extra;
            }
        }

        return notes;
    }

    /// <summary>
    /// Session-scoped delivery cache: any
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

/// <summary>Result of <see cref="IContextEngine.GetMemoryAsync"/>.</summary>
public sealed record MemoryDetail(
    Context.Domain.Entities.Memory Memory,
    IReadOnlyList<MemoryEvidence> Evidence,
    IReadOnlyList<MemoryEntity> Entities);

/// <summary>Result of <see cref="IContextEngine.ProjectInfoAsync"/> — aggregate project stats.</summary>
public sealed record ProjectInfo(
    Guid ProjectId,
    string Name,
    string RootPath,
    int FilesIndexed,
    int SymbolsIndexed,
    int MemoriesCount,
    DateTimeOffset? LastIndexedAt);
