using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public interface IHookInvocationRepository
{
    Task<HookInvocation> RecordAsync(Guid projectId, HookType type, string payloadJson, CancellationToken ct);
    Task<int> CountAsync(Guid projectId, CancellationToken ct);

    /// <summary>Most recent invocations for <paramref name="projectId"/>, newest first, capped at
    /// <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<HookInvocation>> GetRecentAsync(Guid projectId, int limit, CancellationToken ct);
}
