using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Decay;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Periodic background application of <see cref="DecayCalculator"/> across every project's
/// memories.
/// </summary>
/// <remarks>
/// <see cref="Memory"/> has no separate "current relevance" column — <see cref="DecayCalculator"/>
/// is a pure, read-time ranking function that <see cref="ContextEngine.SearchContextAsync"/> and
/// <see cref="ContextEngine.SearchMemoryAsync"/> already apply against <see cref="Memory.Confidence"/>
/// / <see cref="Memory.LastReinforcedAt"/> / <see cref="Memory.Pinned"/> on every query, so nothing
/// needs to be written back just for ranking to reflect decay. What this job does instead is a
/// deliberate, idempotent "soft forget": for a non-pinned, non-superseded, never-reinforced
/// Working-tier memory whose decay factor has fallen below <see cref="DecayThreshold"/> (i.e. it
/// is old and was never reinforced), it permanently folds that decay into
/// <see cref="Memory.Confidence"/> and resets <see cref="Memory.LastReinforcedAt"/> to now — so a
/// stale, never-useful memory's Confidence trends toward zero over successive runs (making it
/// easy for a consumer to filter out) without ever being silently deleted, while a memory that
/// keeps getting reinforced (or is pinned, or is already promoted to LongTerm) is left alone.
/// </remarks>
public sealed class DecayMemoryJobHandler(IProjectRepository projects, IMemoryRepository memories) : IJobHandler
{
    /// <summary>Below this relevance multiplier, an unreinforced Working memory is considered "effectively forgotten".</summary>
    private const double DecayThreshold = 0.15;

    public JobType Type => JobType.DecayMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var allProjects = await projects.ListAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        foreach (var project in allProjects)
        {
            ct.ThrowIfCancellationRequested();

            var projectMemories = await memories.ListByProjectAsync(project.Id, includeSuperseded: false, ct).ConfigureAwait(false);

            foreach (var memory in projectMemories)
            {
                if (memory.Pinned || memory.Tier == MemoryTier.LongTerm || memory.ReinforcementCount > 0)
                {
                    continue; // decay only ever touches memories nothing has vouched for since creation
                }

                var factor = DecayCalculator.ComputeDecayFactor(memory.LastReinforcedAt, memory.Pinned, now);
                if (factor >= DecayThreshold)
                {
                    continue;
                }

                memory.Confidence = Math.Clamp(memory.Confidence * factor, 0.0, 1.0);
                memory.LastReinforcedAt = now; // reset the decay clock now that this run priced it in
                await memories.UpdateAsync(memory, ct).ConfigureAwait(false);
            }
        }
    }
}
