using Context.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Indexing;

/// <summary>
/// The one way a background reindex is started, shared by the Dashboard's manual trigger and
/// <see cref="Scheduling.ReindexSchedulerService"/>. Every run is registered with
/// <see cref="ReindexCancellationRegistry"/> (a scheduled run that wasn't is what made the
/// Dashboard's Stop button 404 against a visibly running reindex) and always ends with a
/// Completed progress event, including on cancellation or failure, so the progress card clears.
/// </summary>
public sealed class ReindexRunner(
    IServiceScopeFactory scopeFactory,
    ReindexCancellationRegistry registry,
    IIndexingProgressReporter progressReporter,
    ILogger<ReindexRunner>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ReindexRunner>.Instance;

    /// <summary>Starts a reindex in the background, cancelling and replacing any run already in
    /// flight for the project (an explicit user request wins).</summary>
    /// <param name="onCompleted">Runs in the reindex's scope after a successful pass.</param>
    public void Start(Guid projectId, bool full, Func<IServiceProvider, Task>? onCompleted = null) =>
        Run(projectId, full, registry.Begin(projectId), onCompleted);

    /// <summary>Like <see cref="Start"/>, but leaves a run already in flight alone.</summary>
    /// <returns>False when the project was already reindexing and nothing was started.</returns>
    public bool TryStart(Guid projectId, bool full)
    {
        if (!registry.TryBegin(projectId, out var ct))
        {
            return false;
        }

        Run(projectId, full, ct, onCompleted: null);
        return true;
    }

    private void Run(Guid projectId, bool full, CancellationToken ct, Func<IServiceProvider, Task>? onCompleted)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            try
            {
                var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
                await pipeline.ReindexProjectAsync(projectId, full, ct).ConfigureAwait(false);
                _logger.LogInformation("Reindex of project {ProjectId} (full={Full}) completed.", projectId, full);

                if (onCompleted is not null)
                {
                    await onCompleted(scope.ServiceProvider).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Reindex of project {ProjectId} (full={Full}) was cancelled.", projectId, full);
                await ReportFinalAsync(new ReindexProgressDto(projectId, 0, 0, Completed: true, Cancelled: true)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reindex of project {ProjectId} (full={Full}) failed.", projectId, full);
                await ReportFinalAsync(new ReindexProgressDto(projectId, 0, 0, Completed: true, Failed: true)).ConfigureAwait(false);
            }
            finally
            {
                registry.End(projectId, ct);
            }
        });
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
