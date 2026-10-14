using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Consolidation;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Runs batch reconsolidation for one project. A dry run stores the plan as a Pending report; a
/// payload with a report id applies that reviewed report, which the apply endpoint has already claimed
/// (Applying), resolving contradictions the code settles; a scheduled run plans and applies at once,
/// only flagging contradictions. An apply ends Applied, or Failed when its last attempt throws or, found at
/// startup, when no queued job will resume it. A Failed report is applied again when its job is requeued.
/// </summary>
public sealed class ReconsolidateMemoryJobHandler(
    MemoryReconsolidator reconsolidator,
    IMemoryRepository memories,
    ILogger<ReconsolidateMemoryJobHandler>? logger = null) : IJobHandler
{
    public JobType Type => JobType.ReconsolidateMemory;

    /// <summary>Marks Failed every Applying report no Pending or Running job carries: a scheduled run
    /// re-plans after a crash instead of resuming, and a claimed report may never have got its job.
    /// Runs before the dispatcher starts; returns how many reports it failed.</summary>
    public static async Task<int> FailOrphanedAppliesAsync(IMemoryRepository memories, IJobRepository jobs, CancellationToken ct)
    {
        var applying = await memories.ListReportsByStatusAsync(ConsolidationReportStatus.Applying, ct).ConfigureAwait(false);
        if (applying.Count == 0)
        {
            return 0;
        }

        var resumable = new HashSet<Guid>();
        foreach (var status in (JobStatus[])[JobStatus.Pending, JobStatus.Running])
        {
            foreach (var job in await jobs.ListByStatusAsync(status, null, ct).ConfigureAwait(false))
            {
                if (job.Type == JobType.ReconsolidateMemory
                    && JsonSerializer.Deserialize<ReconsolidateMemoryPayload>(job.PayloadJson)?.ReportId is { } reportId)
                {
                    resumable.Add(reportId);
                }
            }
        }

        var failed = 0;
        foreach (var report in applying.Where(r => !resumable.Contains(r.Id)))
        {
            if (await memories.SetReportStatusAsync(
                    report.Id, ConsolidationReportStatus.Applying, ConsolidationReportStatus.Failed, null, null, ct).ConfigureAwait(false))
            {
                failed++;
            }
        }

        return failed;
    }

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ReconsolidateMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ReconsolidateMemoryPayload)}.");
        var excluded = payload.ExcludeGroups ?? [];

        if (payload.ReportId is { } reportId)
        {
            var stored = await memories.GetReportAsync(reportId, ct).ConfigureAwait(false);
            if (stored is null || stored.ProjectId != payload.ProjectId)
            {
                throw new InvalidOperationException($"Job {job.Id}: reconsolidation report {reportId} not found for project {payload.ProjectId}.");
            }

            // Applying is also what a crashed or failed attempt leaves behind, and Failed is what a requeue of a
            // permanently failed job finds; re-applying is safe because every group re-validates its sources
            // and skips what already changed.
            var status = stored.Status;
            if (status == ConsolidationReportStatus.Failed
                && await memories.SetReportStatusAsync(reportId, ConsolidationReportStatus.Failed, ConsolidationReportStatus.Applying, null, null, ct).ConfigureAwait(false))
            {
                status = ConsolidationReportStatus.Applying;
            }

            if (status != ConsolidationReportStatus.Applying)
            {
                logger?.LogInformation("Reconsolidation report {ReportId} is {Status}; nothing to apply.", reportId, status);
                return;
            }

            await ApplyAsync(
                job, reportId, payload.ProjectId, ReconsolidationReport.FromJson(stored.ReportJson),
                new ReconsolidationApplyOptions(excluded, ResolveContradictions: true), retryResumesReport: true, ct)
                .ConfigureAwait(false);
            return;
        }

        var plan = await reconsolidator.PlanAsync(payload.ProjectId, ct).ConfigureAwait(false);
        var report = await memories.AddReportAsync(
            new MemoryConsolidationReport
            {
                ProjectId = payload.ProjectId,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = payload.DryRun ? ConsolidationReportStatus.Pending : ConsolidationReportStatus.Applying,
                ReportJson = plan.ToJson(),
            },
            ct).ConfigureAwait(false);

        logger?.LogInformation(
            "Reconsolidation plan {ReportId} for project {ProjectId}: {Groups} groups ({Ambiguous} ambiguous), {Canonicals} canonicals, {Contradictions} contradictions, {ChangeLogs} change logs to retire.",
            report.Id, payload.ProjectId, plan.Stats.Groups, plan.Stats.AmbiguousGroups, plan.Stats.Canonicals, plan.Stats.Contradictions, plan.Stats.RetiredChangeLogs);

        if (payload.DryRun)
        {
            return;
        }

        await ApplyAsync(
            job, report.Id, payload.ProjectId, plan, new ReconsolidationApplyOptions(excluded, ResolveContradictions: false), retryResumesReport: false, ct)
            .ConfigureAwait(false);
    }

    /// <param name="retryResumesReport">True for a reviewed report: the retry names it and applies it again, so a
    /// failure leaves it Applying until the last attempt. A scheduled run re-plans on retry into a new report, so
    /// the report it created is closed as Failed at once instead of being stranded Applying.</param>
    private async Task ApplyAsync(
        Job job, Guid reportId, Guid projectId, ReconsolidationReport plan, ReconsolidationApplyOptions options, bool retryResumesReport, CancellationToken ct)
    {
        ReconsolidationReport applied;
        try
        {
            applied = await reconsolidator.ApplyAsync(projectId, plan, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cancelled apply stays Applying so the re-run after a restart resumes it.
            if (!retryResumesReport || job.Attempts >= JobRetryPolicy.For(job.Type).MaxAttempts)
            {
                await memories.SetReportStatusAsync(
                    reportId, ConsolidationReportStatus.Applying, ConsolidationReportStatus.Failed, null, null, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        var outcome = applied.Outcome!;
        if (!await memories.SetReportStatusAsync(
                reportId, ConsolidationReportStatus.Applying, ConsolidationReportStatus.Applied, outcome.AppliedAt, applied.ToJson(), ct).ConfigureAwait(false))
        {
            logger?.LogWarning("Reconsolidation report {ReportId} stopped being Applying while it was applied.", reportId);
        }

        logger?.LogInformation(
            "Applied reconsolidation report {ReportId}: {Applied} groups applied, {Skipped} skipped, {Excluded} excluded, {ChangeLogs} change logs retired.",
            reportId, outcome.AppliedGroups.Count, outcome.SkippedGroups.Count, outcome.ExcludedGroups.Count, outcome.RetiredChangeLogs);
    }
}
