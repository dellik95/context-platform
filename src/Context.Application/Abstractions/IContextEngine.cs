namespace Context.Application;

/// <summary>
/// The single facade MCP / CLI / API controllers all call — never touch repositories directly.
/// See <see cref="Context.Application.ContextEngine"/> for the default implementation.
/// </summary>
public interface IContextEngine
{
    Task<Context.Retrieval.Abstractions.ContextPack> SearchContextAsync(Context.Retrieval.Abstractions.SearchQuery query, CancellationToken ct);

    Task<object> ExpandAsync(Guid nodeId, Context.Shared.DisclosureLevel level, CancellationToken ct);

    Task<Context.Graph.Abstractions.ImpactSet> GraphImpactAsync(Guid symbolId, int maxHops, CancellationToken ct);

    /// <summary>
    /// Semantic search over the knowledge base, best first: ranked by embedding similarity to the query,
    /// with lexical and entity matches as boosts. <paramref name="topic"/> restricts to memories with that
    /// exact topic (case-insensitive); <paramref name="includeSuperseded"/> adds every non-live status. A
    /// blank query lists the pool by relevance.
    /// </summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(
        Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null);

    /// <summary>The memory with its evidence (oldest first) and entities; null when it doesn't exist
    /// or belongs to another project.</summary>
    Task<MemoryDetail?> GetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct);

    /// <summary>Hard delete of the memory with its evidence, entities and embedding. False when it
    /// doesn't exist or belongs to another project.</summary>
    Task<bool> ForgetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct);

    /// <summary>Queues <see cref="ForgetMemoryAsync"/> as a retryable job and returns the job id. Null
    /// when the memory doesn't exist or belongs to another project (nothing is queued).</summary>
    Task<Guid?> RequestForgetAsync(Guid projectId, Guid memoryId, CancellationToken ct);

    /// <summary><paramref name="cwd"/> is the working directory Claude Code reported at SessionStart,
    /// captured once on creation only, never on the "reuse existing session" path.</summary>
    Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct, string? cwd = null);

    /// <summary>Persists one session event. Once <c>ProjectSettingsDto.MinEventsForMemoryExtraction</c>
    /// events are waiting unclaimed, claims them as one batch and inserts <c>ExtractMemory</c> for it in the same
    /// transaction. <paramref name="eventId"/> and <paramref name="timestamp"/> let a job that replays a hook
    /// keep the event's identity and its original time.</summary>
    Task RecordEventAsync(
        Guid projectId, Guid sessionId, Context.Domain.Enums.SessionEventType type, object payload, CancellationToken ct,
        Guid? eventId = null, DateTimeOffset? timestamp = null);

    /// <summary>Marks the session ended, flushes any still-unclaimed events into a final
    /// <c>ExtractMemory</c> batch, and enqueues the final <c>SummarizeSession</c>.
    /// <paramref name="reason"/> (optional, trailing) is the SessionEnd hook's <c>reason</c> field, persisted verbatim.</summary>
    Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct, string? reason = null);

    /// <summary>Enqueues <c>ExtractMemory</c> over the conversation summary Claude Code wrote when it
    /// compacted the session (the PostCompact hook). No-op for a blank summary.</summary>
    Task ExtractMemoryFromCompactSummaryAsync(Guid projectId, Guid sessionId, string compactSummary, CancellationToken ct);

    /// <summary>
    /// Enqueues <c>SummarizeSession</c> with <c>IsSessionEnd: false</c> — a mid-session checkpoint
    /// (fired by the <c>PostCompact</c> hook), not a final summary. Unlike <see cref="EndSessionAsync"/>,
    /// leaves <see cref="Context.Domain.Entities.Session.Status"/>/<c>EndedAt</c> untouched and still
    /// respects <c>SummarizeSessionJobHandler</c>'s minimum-events gate, same as
    /// <see cref="Context.Application.Scheduling.SessionSummaryScheduler"/>'s periodic pass.
    /// </summary>
    Task RequestSessionSummaryAsync(Guid projectId, Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Hybrid lexical + semantic search over a project's <see cref="Context.Domain.Entities.SessionSummaryChunk"/>s.
    /// <paramref name="sessionId"/> (optional) restricts results to one session's own chunks.
    /// </summary>
    Task<IReadOnlyList<Context.Domain.Entities.SessionSummaryChunk>> SearchSessionSummariesAsync(
        Guid projectId, string query, CancellationToken ct, Guid? sessionId = null, int limit = 20);

    Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Explicit-remember: stores <paramref name="content"/>
    /// as a memory right now, bypassing the normal N-event/session-end extraction triggers entirely -
    /// no <see cref="Context.Domain.Entities.SessionSummaryChunk"/>, no LLM candidate generation. Builds
    /// one <see cref="Context.Memory.Extraction.MemoryCandidate"/> from the raw content (defaulting
    /// <paramref name="type"/> to <see cref="Context.Domain.Enums.MemoryType.Fact"/> when null) and
    /// hands it directly to <see cref="Context.Memory.Abstractions.IMemoryConsolidator"/>, the same
    /// dedup/reinforce/supersede decision the periodic extraction path uses.
    /// <para/>
    /// <paramref name="title"/>/<paramref name="topic"/>/<paramref name="facts"/>/<paramref name="concepts"/>/
    /// <paramref name="filesRead"/>/<paramref name="filesModified"/> let the caller supply the same
    /// structured shape the extraction path produces; omitted, the title is derived from
    /// <paramref name="content"/> and the rest stays empty.
    /// </summary>
    Task<Context.Domain.Entities.Memory> RememberExplicitlyAsync(
        Guid projectId,
        string content,
        Context.Domain.Enums.MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null);

    /// <summary>
    /// Queues <see cref="RememberExplicitlyAsync"/> as a retryable job and returns the job id once the
    /// request is durably stored; the embedding, judging and consolidation happen in the job. Blank
    /// content fails here with an <see cref="ArgumentException"/> instead of burning retries.
    /// </summary>
    Task<Guid> RequestRememberAsync(
        Guid projectId,
        string content,
        Context.Domain.Enums.MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null);
}
