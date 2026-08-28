using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>Marks a file deleted and tears down everything owned by it (chunks, symbols, graph nodes/edges).</summary>
public sealed class DeleteFileJobHandler(
    IFileRepository files,
    ISymbolRepository symbols,
    IChunkRepository chunks,
    IJobQueue jobQueue) : IJobHandler
{
    public JobType Type => JobType.DeleteFile;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<DeleteFilePayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(DeleteFilePayload)}.");

        var file = await files.GetByRelativePathAsync(payload.ProjectId, payload.RelativePath, ct).ConfigureAwait(false);
        if (file is null || file.IsDeleted)
        {
            return;
        }

        // Chunks first (Chunk.SymbolId is FK-restricted to Symbol), then symbols.
        await chunks.ReplaceForFileAsync(file.Id, [], ct).ConfigureAwait(false);
        await symbols.ReplaceForFileAsync(file.Id, [], ct).ConfigureAwait(false);
        await jobQueue.EnqueueAsync(JobType.UpdateGraph, new UpdateGraphPayload(payload.ProjectId, file.Id, Invalidate: true), ct: ct)
            .ConfigureAwait(false);
        await files.MarkDeletedAsync(file.Id, ct).ConfigureAwait(false);
    }
}
