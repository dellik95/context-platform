using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Seam for persisting per-call LLM token-usage records (BACKLOG-27), implemented against the
/// real repository by Context.Application. Mirrors Context.Infrastructure's own IUsageRecorder
/// but is kept as a separate interface for the same reason as ISessionEventSource/IMemoryStore
/// above: Context.Memory has no dependency on Context.Infrastructure/EF Core.
/// </summary>
public interface IUsageRecorder
{
    Task RecordAsync(UsageRecord record, CancellationToken ct);
}
