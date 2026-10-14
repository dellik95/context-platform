using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public interface IHookInvocationRepository
{
    /// <summary>Stores the invocation under <paramref name="id"/>, the id of the job that processed it, so the
    /// row doubles as that job's "already processed" marker (see <see cref="ExistsAsync"/>).</summary>
    Task<HookInvocation> RecordAsync(Guid id, Guid projectId, HookType type, string payloadJson, DateTimeOffset receivedAt, CancellationToken ct);

    Task<bool> ExistsAsync(Guid id, CancellationToken ct);
    Task<int> CountAsync(Guid projectId, CancellationToken ct);

    /// <summary>Most recent invocations for <paramref name="projectId"/>, newest first, capped at
    /// <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<HookInvocation>> GetRecentAsync(Guid projectId, int limit, CancellationToken ct);

    /// <summary>Deletes every invocation for <paramref name="projectId"/> received at or before
    /// <paramref name="upTo"/>.</summary>
    Task DeleteOlderThanAsync(Guid projectId, DateTimeOffset upTo, CancellationToken ct);
}
