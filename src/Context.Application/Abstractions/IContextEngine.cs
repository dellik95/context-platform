namespace Context.Application;

/// <summary>
/// The single facade MCP / CLI / API controllers all call — never touch repositories directly.
/// See CONTRACTS.md §10 (fixed shape) and <see cref="Context.Application.ContextEngine"/> for the
/// default implementation.
/// </summary>
public interface IContextEngine
{
    Task<Context.Retrieval.Abstractions.ContextPack> SearchContextAsync(Context.Retrieval.Abstractions.SearchQuery query, CancellationToken ct);

    Task<object> ExpandAsync(Guid nodeId, Context.Shared.DisclosureLevel level, CancellationToken ct);

    Task<Context.Graph.Abstractions.ImpactSet> GraphImpactAsync(Guid symbolId, int maxHops, CancellationToken ct);

    /// <summary>
    /// <paramref name="topic"/> (optional, added alongside <see cref="Context.Domain.Entities.Memory.Topic"/>)
    /// additionally restricts results to memories tagged with that topic — see
    /// <see cref="Context.Infrastructure.Abstractions.IMemoryRepository.SearchAsync"/> for the
    /// exact matching rule. Trailing/optional rather than inserted earlier in the parameter list
    /// so every existing positional call site (CONTRACTS.md §10's original fixed shape) keeps compiling.
    /// </summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null);

    /// <paramref name="cwd"/> (optional, trailing per CONTRACTS.md §10's fixed-shape convention above)
    /// is the working directory Claude Code reported at SessionStart -- captured once, on creation
    /// only, never on the "reuse existing session" path.
    Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct, string? cwd = null);

    Task RecordEventAsync(Guid sessionId, Context.Domain.Enums.SessionEventType type, object payload, CancellationToken ct);

    /// <summary>Enqueues <c>SummarizeSession</c>, which itself chains into <c>ExtractMemory</c> once the summary is ready.
    /// <paramref name="reason"/> (optional, trailing) is the SessionEnd hook's <c>reason</c> field, persisted verbatim.</summary>
    Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct, string? reason = null);

    Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Explicit-remember (docs/architecture/MEMORY_PIPELINE_PLAN.md §F): stores <paramref name="content"/>
    /// as a memory right now, bypassing the normal N-event/session-end extraction triggers entirely -
    /// no <see cref="Context.Domain.Entities.SessionSummaryChunk"/>, no LLM candidate generation. Builds
    /// one <see cref="Context.Memory.Extraction.MemoryCandidate"/> from the raw content (defaulting
    /// <paramref name="type"/> to <see cref="Context.Domain.Enums.MemoryType.Instruction"/> when null) and
    /// hands it directly to <see cref="Context.Memory.Abstractions.IMemoryConsolidator"/>, the same
    /// dedup/reinforce/supersede decision the periodic extraction path uses.
    /// <para/>
    /// <paramref name="title"/>/<paramref name="topic"/>/<paramref name="facts"/>/<paramref name="concepts"/>/
    /// <paramref name="filesRead"/>/<paramref name="filesModified"/> let the caller (an agent that
    /// already has the full context, not a second LLM re-deriving structure from prose) supply the
    /// same structured shape <c>LlmMemoryExtractor</c>'s periodic path produces instead of leaving
    /// this an unstructured narrative dump — see <c>MemoryExtractionPromptBuilder</c> for the exact
    /// TOPIC/FACTS/CONCEPTS semantics and the fixed CONCEPTS taxonomy. All optional and trailing
    /// (after <paramref name="ct"/>, matching <see cref="SearchMemoryAsync"/>'s own precedent) so
    /// every existing positional call site keeps compiling; omitted ones fall back to today's
    /// behavior (title auto-derived from <paramref name="content"/>, everything else empty/null).
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
}
