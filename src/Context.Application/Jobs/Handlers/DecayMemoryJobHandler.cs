using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Decay;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Retires memories <see cref="DecayCalculator.IsExpired"/> says have run their course: Working-tier
/// memories whose decayed relevance fell below the threshold.
/// They become <see cref="MemoryStatus.Obsolete"/> (kept for history, dropped from the vector index) and
/// release the contest partners they leave uncontested; nothing is deleted. Ranking decay itself is
/// applied at read time and needs no write-back.
/// </summary>
public sealed class DecayMemoryJobHandler(
    IProjectRepository projects,
    IMemoryRepository memories,
    IMemoryStore store,
    ILogger<DecayMemoryJobHandler>? logger = null) : IJobHandler
{
    public JobType Type => JobType.DecayMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var project in await projects.ListAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var retired = 0;
            foreach (var memory in await memories.ListByProjectAsync(project.Id, includeSuperseded: false, ct).ConfigureAwait(false))
            {
                if (DecayCalculator.IsExpired(memory, now) && await MemoryContests.RetireAsync(store, memory.Id, ct).ConfigureAwait(false))
                {
                    retired++;
                }
            }

            if (retired > 0)
            {
                logger?.LogInformation("Marked {Count} decayed memories obsolete for project {ProjectId}.", retired, project.Id);
            }
        }
    }
}
