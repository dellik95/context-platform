using System.Text.Json;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Reconciles a project's sqlite-vec indexes with its database. The vector stores are separate tables with no
/// foreign keys, written after the database commit, so a failed write or delete is only logged where it happens;
/// this job is what repairs the difference. It removes chunk and memory vectors whose row is gone (they would keep
/// occupying slots of the nearest-neighbour search) and rebuilds the vector of every session-summary chunk that
/// lacks one for the current embedding provider (nothing else ever does). The three steps run independently, so one
/// failing does not stop the others, and the job fails afterwards so its retry covers the rest.
/// </summary>
public sealed class RepairVectorsJobHandler(
    IChunkRepository chunks,
    IChunkVectorIndex chunkVectors,
    IMemoryRepository memories,
    IMemoryVectorIndex memoryVectors,
    ISessionRepository sessions,
    ISessionSummaryVectorIndex summaryVectors,
    IProviderResolver providerResolver,
    ILogger<RepairVectorsJobHandler>? logger = null) : IJobHandler
{
    private const int EmbeddingBatchSize = 16;

    private readonly ILogger _logger = logger ?? NullLogger<RepairVectorsJobHandler>.Instance;

    public JobType Type => JobType.RepairVectors;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<RepairVectorsPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(RepairVectorsPayload)}.");
        var projectId = payload.ProjectId;

        var failures = new List<Exception>();
        await RunAsync(() => SweepChunkVectorsAsync(projectId, ct), failures).ConfigureAwait(false);
        await RunAsync(() => SweepMemoryVectorsAsync(projectId, ct), failures).ConfigureAwait(false);
        await RunAsync(() => BackfillSummaryVectorsAsync(projectId, ct), failures).ConfigureAwait(false);

        if (failures.Count > 0)
        {
            throw new AggregateException($"Vector repair for project {projectId} did not finish.", failures);
        }
    }

    private async Task SweepChunkVectorsAsync(Guid projectId, CancellationToken ct)
    {
        var indexed = await chunkVectors.ListChunkIdsAsync(projectId, ct).ConfigureAwait(false);
        var existing = await chunks.ListIdsByProjectAsync(projectId, ct).ConfigureAwait(false);
        Guid[] orphans = [.. indexed.Where(id => !existing.Contains(id))];
        if (orphans.Length > 0)
        {
            await chunkVectors.DeleteAsync(orphans, ct).ConfigureAwait(false);
            _logger.LogInformation("Removed {Count} chunk vectors of project {ProjectId} whose chunk no longer exists.", orphans.Length, projectId);
        }
    }

    private async Task SweepMemoryVectorsAsync(Guid projectId, CancellationToken ct)
    {
        var indexed = await memoryVectors.ListMemoryIdsAsync(projectId, ct).ConfigureAwait(false);
        var live = (await memories.ListByProjectAsync(projectId, includeSuperseded: false, ct).ConfigureAwait(false)).Select(m => m.Id).ToHashSet();
        Guid[] orphans = [.. indexed.Where(id => !live.Contains(id))];
        if (orphans.Length > 0)
        {
            await memoryVectors.DeleteAsync(orphans, ct).ConfigureAwait(false);
            _logger.LogInformation("Removed {Count} memory vectors of project {ProjectId} whose memory is no longer live.", orphans.Length, projectId);
        }
    }

    private async Task BackfillSummaryVectorsAsync(Guid projectId, CancellationToken ct)
    {
        var summaryChunks = await sessions.ListSummaryChunksByProjectAsync(projectId, ct).ConfigureAwait(false);
        if (summaryChunks.Count == 0)
        {
            return;
        }

        var generator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var provider = generator.GetProviderModelKey();
        var indexed = await summaryVectors.ListChunkIdsAsync(projectId, provider, ct).ConfigureAwait(false);

        var missing = summaryChunks
            .Where(c => !indexed.Contains(c.Id))
            .Select(c => (Chunk: c, Text: SessionSummaryEmbeddingText.For(c)))
            .Where(x => x.Text is not null)
            .ToList();

        foreach (var batch in missing.Chunk(EmbeddingBatchSize))
        {
            var embeddings = await generator.GenerateAsync(batch.Select(x => x.Text!), cancellationToken: ct).ConfigureAwait(false);
            for (var i = 0; i < batch.Length && i < embeddings.Count; i++)
            {
                await summaryVectors.UpsertAsync(
                    batch[i].Chunk.Id, projectId, batch[i].Chunk.SessionId, provider, embeddings[i].Vector.ToArray(), ct).ConfigureAwait(false);
            }
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation("Rebuilt {Count} session-summary vectors of project {ProjectId}.", missing.Count, projectId);
        }
    }

    private async Task RunAsync(Func<Task> step, List<Exception> failures)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "A vector repair step failed.");
            failures.Add(ex);
        }
    }
}
