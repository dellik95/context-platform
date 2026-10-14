using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Indexing;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Runs a project reindex as a job. The run is registered with <see cref="ReindexCancellationRegistry"/> so the
/// dashboard's Stop button reaches it, and a newer request for the project cancels it. A pass the user cancelled
/// ends the job normally; a host shutdown or a crash requeues it. Every pass ends with a Completed progress
/// event, including on cancellation or failure, so the progress card clears.
/// </summary>
public sealed class ReindexProjectJobHandler(
    IIndexingPipeline pipeline,
    ReindexCancellationRegistry registry,
    IIndexingProgressReporter progressReporter,
    IProjectStatusPublisher? statusPublisher = null,
    ILogger<ReindexProjectJobHandler>? logger = null) : IJobHandler
{
    private readonly IProjectStatusPublisher _statusPublisher = statusPublisher ?? NullProjectStatusPublisher.Instance;
    private readonly ILogger _logger = logger ?? NullLogger<ReindexProjectJobHandler>.Instance;

    public JobType Type => JobType.ReindexProject;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ReindexProjectPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ReindexProjectPayload)}.");
        var projectId = payload.ProjectId;

        var registryToken = registry.Begin(projectId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, registryToken);
        try
        {
            await pipeline.ReindexProjectAsync(projectId, payload.Full, linked.Token).ConfigureAwait(false);
            _logger.LogInformation("Reindex of project {ProjectId} (full={Full}) completed.", projectId, payload.Full);
            await PublishStatusAsync(projectId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && registryToken.IsCancellationRequested)
        {
            _logger.LogInformation("Reindex of project {ProjectId} (full={Full}) was cancelled.", projectId, payload.Full);
            await ReportFinalAsync(new ReindexProgressDto(projectId, 0, 0, Completed: true, Cancelled: true)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reindex of project {ProjectId} (full={Full}) failed.", projectId, payload.Full);
            await ReportFinalAsync(new ReindexProgressDto(projectId, 0, 0, Completed: true, Failed: true)).ConfigureAwait(false);
            throw;
        }
        finally
        {
            registry.End(projectId, registryToken);
        }
    }

    private async Task PublishStatusAsync(Guid projectId)
    {
        try
        {
            await _statusPublisher.PublishAsync(projectId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish the status of project {ProjectId} after its reindex.", projectId);
        }
    }

    private async Task ReportFinalAsync(ReindexProgressDto progress)
    {
        try
        {
            await progressReporter.ReportAsync(progress, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not report the final reindex state for project {ProjectId}.", progress.ProjectId);
        }
    }
}
