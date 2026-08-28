using System.Text.Json;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// <c>ParseFile</c> shares its whole job payload shape and effective work with <c>IndexFile</c>
/// (parse + symbol-level diff is a step inside <see cref="IIndexingPipeline.IndexFileAsync"/>,
/// not a separately invokable phase — see <see cref="Indexing.IndexingPipeline"/>'s remarks). This
/// handler exists so a caller that specifically wants "reparse this file" (e.g. a future file
/// watcher reacting to an on-disk change, independent from an <c>IndexFile</c> job already queued
/// for the same file) has a <see cref="Domain.Enums.JobType.ParseFile"/> entry point without
/// duplicating the pipeline logic.
/// </summary>
public sealed class ParseFileJobHandler(IIndexingPipeline pipeline) : IJobHandler
{
    public JobType Type => JobType.ParseFile;

    public Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<IndexFilePayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(IndexFilePayload)}.");

        return pipeline.IndexFileAsync(payload.ProjectId, payload.RelativePath, ct);
    }
}
