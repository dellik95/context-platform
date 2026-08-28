using System.Text.Json;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Application.Jobs.Handlers;

/// <summary>Runs the full indexing pipeline for one file (hash-check -> parse -> diff -> persist -> enqueue follow-on jobs).</summary>
public sealed class IndexFileJobHandler(IIndexingPipeline pipeline) : IJobHandler
{
    public JobType Type => JobType.IndexFile;

    public Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<IndexFilePayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(IndexFilePayload)}.");

        return pipeline.IndexFileAsync(payload.ProjectId, payload.RelativePath, ct);
    }
}
