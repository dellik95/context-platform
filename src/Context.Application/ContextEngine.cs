using Context.Application.Abstractions;
using Context.Application.Engine;
using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Shared;
using Microsoft.Extensions.Logging;

namespace Context.Application;

/// <summary>
/// Default <see cref="IContextEngine"/>: thin facade that coordinates focused handlers for pack
/// assembly, progressive node expansion, memory search/mutations, and session lifecycle.
/// </summary>
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
    IUnitOfWork unitOfWork,
    IJobRepository jobRepository,
    ILogger<ContextEngine>? logger = null) : IContextEngine
{
    private readonly ContextPackDeliveryCoordinator _packDelivery = CreatePackDeliveryCoordinator(
        contextPackBuilder, memoryRepository, projectSettingsService, deliveryCache, memoryStore, providerResolver, logger);
    private readonly MemoryCoordinator _memories = CreateMemoryCoordinator(
        memoryRepository, memoryStore, memoryConsolidator, jobQueue, providerResolver, logger);
    private readonly SessionCoordinator _sessions = new(
        sessionRepository, sessionSummaryVectorIndex, providerResolver, projectSettingsService, unitOfWork, jobRepository, jobQueue);
    private readonly NodeExpansionCoordinator _expansion = new(
        graphTraversal, graphCacheProvider, projectRepository, fileRepository, symbolRepository, chunkRepository, memoryRepository);

    private static ContextPackDeliveryCoordinator CreatePackDeliveryCoordinator(
        ContextPackBuilder contextPackBuilder,
        IMemoryRepository memoryRepository,
        IProjectSettingsService projectSettingsService,
        SessionDeliveryCache deliveryCache,
        IMemoryStore memoryStore,
        IProviderResolver providerResolver,
        ILogger? logger)
    {
        var search = new MemorySearchHelper(memoryRepository, memoryStore, providerResolver, logger);
        return new ContextPackDeliveryCoordinator(contextPackBuilder, memoryRepository, projectSettingsService, deliveryCache, search);
    }

    private static MemoryCoordinator CreateMemoryCoordinator(
        IMemoryRepository memoryRepository,
        IMemoryStore memoryStore,
        IMemoryConsolidator memoryConsolidator,
        IJobQueue jobQueue,
        IProviderResolver providerResolver,
        ILogger? logger)
    {
        var search = new MemorySearchHelper(memoryRepository, memoryStore, providerResolver, logger);
        return new MemoryCoordinator(memoryRepository, memoryStore, memoryConsolidator, jobQueue, search);
    }

    public Task<ContextPack> SearchContextAsync(SearchQuery query, CancellationToken ct) =>
        _packDelivery.SearchContextAsync(query, ct);

    public Task<object> ExpandAsync(Guid nodeId, DisclosureLevel level, CancellationToken ct) =>
        _expansion.ExpandAsync(nodeId, level, ct);

    public Task<ImpactSet> GraphImpactAsync(Guid symbolId, int maxHops, CancellationToken ct) =>
        _expansion.GraphImpactAsync(symbolId, maxHops, ct);

    public Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(
        Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null) =>
        _memories.SearchMemoryAsync(projectId, query, includeSuperseded, ct, topic);

    public Task<MemoryDetail?> GetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct) =>
        _memories.GetMemoryAsync(projectId, memoryId, ct);

    public Task<bool> ForgetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct) =>
        _memories.ForgetMemoryAsync(projectId, memoryId, ct);

    public Task<Guid?> RequestForgetAsync(Guid projectId, Guid memoryId, CancellationToken ct) =>
        _memories.RequestForgetAsync(projectId, memoryId, ct);

    public Task<IReadOnlyList<SessionSummaryChunk>> SearchSessionSummariesAsync(
        Guid projectId, string query, CancellationToken ct, Guid? sessionId = null, int limit = 20) =>
        _sessions.SearchSessionSummariesAsync(projectId, query, ct, sessionId, limit);

    public Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct, string? cwd = null) =>
        _sessions.StartSessionAsync(projectId, claudeSessionId, ct, cwd);

    public Task RecordEventAsync(
        Guid projectId, Guid sessionId, SessionEventType type, object payload, CancellationToken ct,
        Guid? eventId = null, DateTimeOffset? timestamp = null) =>
        _sessions.RecordEventAsync(projectId, sessionId, type, payload, ct, eventId, timestamp);

    public Task ExtractMemoryFromCompactSummaryAsync(Guid projectId, Guid sessionId, string compactSummary, CancellationToken ct) =>
        _sessions.ExtractMemoryFromCompactSummaryAsync(projectId, sessionId, compactSummary, ct);

    public Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct, string? reason = null) =>
        _sessions.EndSessionAsync(projectId, sessionId, ct, reason);

    public Task RequestSessionSummaryAsync(Guid projectId, Guid sessionId, CancellationToken ct) =>
        _sessions.RequestSessionSummaryAsync(projectId, sessionId, ct);

    public Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct) =>
        _expansion.ProjectInfoAsync(projectId, ct);

    public Task<Context.Domain.Entities.Memory> RememberExplicitlyAsync(
        Guid projectId,
        string content,
        MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null) =>
        _memories.RememberExplicitlyAsync(projectId, content, type, ct, title, topic, facts, concepts, filesRead, filesModified);

    public Task<Guid> RequestRememberAsync(
        Guid projectId,
        string content,
        MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null) =>
        _memories.RequestRememberAsync(projectId, content, type, ct, title, topic, facts, concepts, filesRead, filesModified);
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
