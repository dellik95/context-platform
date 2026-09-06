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

    Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct);

    Task RecordEventAsync(Guid sessionId, Context.Domain.Enums.SessionEventType type, object payload, CancellationToken ct);

    /// <summary>Enqueues <c>SummarizeSession</c>, which itself chains into <c>ExtractMemory</c> once the summary is ready.</summary>
    Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct);

    Task<object> ProjectInfoAsync(Guid projectId, CancellationToken ct);
}
