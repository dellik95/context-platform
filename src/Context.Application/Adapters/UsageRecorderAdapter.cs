using Context.Domain.Entities;
using Context.Memory.Abstractions;
using InfrastructureUsageRecorder = Context.Infrastructure.Abstractions.IUsageRecorder;

namespace Context.Application.Adapters;

/// <summary>Adapts Context.Memory's <see cref="IUsageRecorder"/> seam onto the real
/// <see cref="InfrastructureUsageRecorder"/> repository.</summary>
public sealed class UsageRecorderAdapter(InfrastructureUsageRecorder inner) : IUsageRecorder
{
    public Task RecordAsync(UsageRecord record, CancellationToken ct) => inner.RecordAsync(record, ct);
}
