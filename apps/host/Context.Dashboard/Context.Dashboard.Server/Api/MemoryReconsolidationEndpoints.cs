using Context.Application.Jobs;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Consolidation;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// Batch memory reconsolidation: enqueue a run (a dry run by default, which only stores a Pending
/// report), review reports, apply or discard a Pending one, and delete finished ones from the list. Apply claims the report (Pending to
/// Applying) before enqueueing, so a report is applied at most once. A report of another project
/// reports 404, like <see cref="MemoryEndpoints"/>.
/// </summary>
public static class MemoryReconsolidationEndpoints
{
    public static void MapMemoryReconsolidationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/memories/reconsolidation").RequireBearerToken().RequireMatchingProjectId();
        group.MapPost("", EnqueueAsync);
        group.MapGet("/reports", ListAsync);
        group.MapGet("/reports/{id:guid}", GetAsync);
        group.MapPost("/reports/{id:guid}/apply", ApplyAsync);
        group.MapPost("/reports/{id:guid}/discard", DiscardAsync);
        group.MapDelete("/reports/{id:guid}", DeleteReportAsync);
        group.MapDelete("/reports", CleanupReportsAsync);
    }

    private static async Task<IResult> EnqueueAsync(
        Guid? projectId, bool? dryRun, IJobQueue jobQueue, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var id = projectId ?? currentProject.ProjectId;
        var payload = new ReconsolidateMemoryPayload(id, DryRun: dryRun ?? true);
        var jobId = await jobQueue.EnqueueAsync(JobType.ReconsolidateMemory, payload, id, JobPriority.Session, ct).ConfigureAwait(false);
        return Results.Accepted(value: new ReconsolidationTriggerResponse(id, jobId, payload.DryRun, null, "queued"));
    }

    private static async Task<IResult> ListAsync(
        Guid? projectId, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var reports = await memories.ListReportsAsync(projectId ?? currentProject.ProjectId, ct).ConfigureAwait(false);
        return Results.Ok(reports.Select(r => new ReconsolidationReportSummaryDto(
            r.Id, r.CreatedAt, r.Status, r.AppliedAt, ReconsolidationReport.FromJson(r.ReportJson).Stats)).ToList());
    }

    private static async Task<IResult> GetAsync(
        Guid id, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var report = await FindAsync(id, memories, currentProject, ct).ConfigureAwait(false);
        return report is null
            ? Results.NotFound()
            : Results.Ok(new ReconsolidationReportDto(report.Id, report.CreatedAt, report.Status, report.AppliedAt, ReconsolidationReport.FromJson(report.ReportJson)));
    }

    private static async Task<IResult> ApplyAsync(
        Guid id, ApplyReconsolidationRequest? request, IMemoryRepository memories, IUnitOfWork unitOfWork, IJobRepository jobs, IJobQueue jobQueue,
        CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var report = await FindAsync(id, memories, currentProject, ct).ConfigureAwait(false);
        if (report is null)
        {
            return Results.NotFound();
        }

        // Claiming the report and inserting its job commit together: a failed insert leaves the report Pending.
        var job = JobFactory.Create(
            JobType.ReconsolidateMemory,
            new ReconsolidateMemoryPayload(report.ProjectId, DryRun: false, ReportId: id, ExcludeGroups: request?.ExcludeGroups),
            report.ProjectId,
            JobPriority.Session);
        var claimed = await unitOfWork.InTransactionAsync(async () =>
        {
            if (!await memories.SetReportStatusAsync(id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Applying, null, null, ct).ConfigureAwait(false))
            {
                return false;
            }

            await jobs.EnqueueAsync(job, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

        if (!claimed)
        {
            return Results.Conflict(new { message = $"Report '{id}' is no longer Pending; only a Pending report can be applied." });
        }

        await jobQueue.WakeAsync(report.ProjectId, ct).ConfigureAwait(false);
        return Results.Accepted(value: new ReconsolidationTriggerResponse(report.ProjectId, job.Id, false, id, "queued"));
    }

    private static async Task<IResult> DiscardAsync(
        Guid id, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var report = await FindAsync(id, memories, currentProject, ct).ConfigureAwait(false);
        if (report is null)
        {
            return Results.NotFound();
        }

        return await memories.SetReportStatusAsync(id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Discarded, null, null, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.Conflict(new { message = $"Report '{id}' is no longer Pending; only a Pending report can be discarded." });
    }

    /// <summary>Removes one report from the list. An Applying report is owned by its apply job, so it answers 409.</summary>
    private static async Task<IResult> DeleteReportAsync(
        Guid id, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        if (await FindAsync(id, memories, currentProject, ct).ConfigureAwait(false) is null)
        {
            return Results.NotFound();
        }

        return await memories.DeleteReportAsync(id, ct).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.Conflict(new { message = $"Report '{id}' is being applied and cannot be deleted yet." });
    }

    /// <summary>Clears finished reports from the list: with no <paramref name="status"/> every Applied, Discarded and
    /// Failed one, keeping Pending reports that still await review. Applying is never deletable.</summary>
    private static async Task<IResult> CleanupReportsAsync(
        ConsolidationReportStatus? status, Guid? projectId, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        if (status == ConsolidationReportStatus.Applying)
        {
            return Results.BadRequest(new { message = "A report that is being applied cannot be deleted." });
        }

        IReadOnlyCollection<ConsolidationReportStatus> statuses = status is { } one
            ? [one]
            : [ConsolidationReportStatus.Applied, ConsolidationReportStatus.Discarded, ConsolidationReportStatus.Failed];
        var removed = await memories.DeleteReportsAsync(projectId ?? currentProject.ProjectId, statuses, ct).ConfigureAwait(false);
        return Results.Ok(new { removed });
    }

    private static async Task<MemoryConsolidationReport?> FindAsync(
        Guid id, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var report = await memories.GetReportAsync(id, ct).ConfigureAwait(false);
        return report is not null && report.ProjectId == currentProject.ProjectId ? report : null;
    }
}
