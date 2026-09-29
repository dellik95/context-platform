using Context.Application.Jobs;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>/api/jobs</c> and <c>/api/logs/tail</c> — operational visibility for the Dashboard's System
/// tab: which jobs failed and why, a manual re-trigger for a permanently
/// <see cref="JobStatus.Failed"/> job, and a tail of this host's own log file. All three are the
/// read/act surface over what <see cref="JobDispatcher"/> and Serilog already track — no new
/// tracking is introduced here.
/// <para/>
/// <c>projectId</c> is optional on every job route but never widening: supplied, it must name the
/// caller's own project (<see cref="ProjectScopeAuth.RequireMatchingProjectIdFilter"/>); omitted, it
/// defaults to that same project rather than to every project on the host. Host-wide maintenance
/// jobs (<see cref="Job.ProjectId"/> null — backup, memory decay) therefore don't appear in this
/// per-project list; they are visible to <see cref="RetryAsync"/> by id, since
/// <see cref="IJobRepository.GetByIdAsync"/> treats a host-wide job as belonging to every project.
/// </summary>
public static class JobsEndpoints
{
    private const int DefaultListLimit = 50;
    private const int DefaultTailLines = 200;
    private const int MaxTailLines = 5000;

    public static void MapJobsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireBearerToken().RequireMatchingProjectId();
        group.MapGet("/api/jobs", ListAsync);
        group.MapPost("/api/jobs/{id:guid}/retry", RetryAsync);
        group.MapPost("/api/jobs/retry-all", RetryAllAsync);
        group.MapPost("/api/jobs/{id:guid}/force", ForceAsync);
        group.MapPost("/api/jobs/force-running", ForceRunningAsync);
        group.MapPost("/api/jobs/cleanup", CleanupAsync);
        group.MapGet("/api/logs/tail", TailLogAsync);
    }

    private static async Task<IResult> ListAsync(
        JobStatus? status, Guid? projectId, int? limit, IJobRepository jobs,
        CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        // Failed is the primary use case this feature exists for (finding/retrying broken jobs),
        // so it's the default when the caller doesn't ask for a specific status.
        var effectiveStatus = status ?? JobStatus.Failed;
        var effectiveLimit = limit is > 0 ? limit.Value : DefaultListLimit;

        var results = await jobs
            .ListByStatusAsync(effectiveStatus, projectId ?? currentProject.ProjectId, ct)
            .ConfigureAwait(false);
        return Results.Ok(results.Take(effectiveLimit).Select(JobDto.FromEntity));
    }

    private static async Task<IResult> RetryAsync(
        Guid id, IJobRepository jobs, IJobQueue jobQueue, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        // Existence is checked against the caller's own project first, so a job id belonging to
        // another project is 404 rather than something this caller can requeue.
        if (await jobs.GetByIdAsync(id, ct, currentProject.ProjectId).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new { message = $"No job with id '{id}' exists." });
        }

        try
        {
            await jobQueue.RequeueAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (JobNotFoundException)
        {
            return Results.NotFound(new { message = $"No job with id '{id}' exists." });
        }
        catch (JobStatusConflictException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// System tab "retry all" — requeues every currently <see cref="JobStatus.Failed"/> job
    /// (optionally scoped to one project) instead of clicking each one individually. Loads the
    /// list fresh right before requeuing rather than reusing whatever the caller already had
    /// rendered, so a job that finished/changed status between page load and this click is simply
    /// skipped (not retryable) instead of erroring the whole batch.
    /// </summary>
    private static async Task<IResult> RetryAllAsync(
        Guid? projectId, IJobRepository jobs, IJobQueue jobQueue,
        CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var failed = await jobs
            .ListByStatusAsync(JobStatus.Failed, projectId ?? currentProject.ProjectId, ct)
            .ConfigureAwait(false);

        var retried = 0;
        foreach (var job in failed)
        {
            try
            {
                await jobQueue.RequeueAsync(job.Id, ct).ConfigureAwait(false);
                retried++;
            }
            catch (JobStatusConflictException)
            {
                // Raced with something else already advancing this job's status -- skip it, don't
                // fail the whole batch over one job.
            }
        }

        return Results.Ok(new { retried, total = failed.Count });
    }

    /// <summary>
    /// Runs one job right now, outside its project lane (see <see cref="IJobQueue.ForceRunAsync"/>) —
    /// for a job stuck Running behind a hung handler, or Pending behind a lane full of them.
    /// </summary>
    private static async Task<IResult> ForceAsync(
        Guid id, IJobRepository jobs, IJobQueue jobQueue, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        if (await jobs.GetByIdAsync(id, ct, currentProject.ProjectId).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new { message = $"No job with id '{id}' exists." });
        }

        try
        {
            await jobQueue.ForceRunAsync(id, ct).ConfigureAwait(false);
            return Results.Accepted();
        }
        catch (JobNotFoundException)
        {
            return Results.NotFound(new { message = $"No job with id '{id}' exists." });
        }
        catch (JobStatusConflictException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
    }

    /// <summary>Force-runs every job of the project currently marked Running, concurrently so
    /// handlers that ignore cancellation don't serialize their supersede waits.</summary>
    private static async Task<IResult> ForceRunningAsync(
        Guid? projectId, IJobRepository jobs, IJobQueue jobQueue,
        CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var running = await jobs
            .ListByStatusAsync(JobStatus.Running, projectId ?? currentProject.ProjectId, ct)
            .ConfigureAwait(false);

        var outcomes = await Task.WhenAll(running.Select(async job =>
        {
            try
            {
                await jobQueue.ForceRunAsync(job.Id, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is JobStatusConflictException or JobNotFoundException)
            {
                return false;
            }
        })).ConfigureAwait(false);

        return Results.Ok(new { forced = outcomes.Count(forced => forced), total = running.Count });
    }

    /// <summary>Statuses this endpoint is willing to bulk-delete. Running is excluded outright — a
    /// row backing an in-flight job must never be deleted out from under its worker. Failed is
    /// excluded too: it's still actionable via retry/diagnose, and has no bulk-delete ask today
    /// (only "retry all").</summary>
    private static readonly JobStatus[] CleanableStatuses = [JobStatus.Done, JobStatus.Pending];

    /// <summary>
    /// System tab "clean up job queue" — bulk-deletes every job in <paramref name="status"/>
    /// (default <see cref="JobStatus.Done"/>, optionally scoped to one project). Pending is also
    /// allowed: a backlog built up by a since-fixed bug (e.g. a bad embedding model config that
    /// queued thousands of doomed retries) has nothing worth keeping either — but unlike Done, a
    /// Pending row can still represent real, not-yet-attempted work, so the caller opts into it
    /// explicitly rather than it being bundled into the default cleanup.
    /// </summary>
    private static async Task<IResult> CleanupAsync(
        JobStatus? status, Guid? projectId, IJobRepository jobs, IJobCountsNotifier jobCountsNotifier,
        CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var effectiveStatus = status ?? JobStatus.Done;
        if (!CleanableStatuses.Contains(effectiveStatus))
        {
            return Results.BadRequest(new
            {
                message = $"Cannot bulk-delete jobs in status '{effectiveStatus}'. Allowed: {string.Join(", ", CleanableStatuses)}."
            });
        }

        var scopedProjectId = projectId ?? currentProject.ProjectId;
        var removed = await jobs.DeleteByStatusAsync(effectiveStatus, scopedProjectId, ct).ConfigureAwait(false);

        // Same per-project counts push JobDispatcher does after every status change -- otherwise the
        // System tab's stat tiles stay stale (showing the pre-cleanup count) until its next slow
        // reconciliation poll.
        var counts = await jobs.CountByStatusAsync(scopedProjectId, ct).ConfigureAwait(false);
        await jobCountsNotifier.NotifyAsync(
            scopedProjectId, new JobCounts(counts.Pending, counts.Running, counts.Failed, counts.Done), ct).ConfigureAwait(false);

        return Results.Ok(new { removed, status = effectiveStatus });
    }

    /// <summary>
    /// Tails the most-recently-written Serilog output file under <c>{ProjectDataDir}/logs</c> (see
    /// Program.cs's <c>WriteTo.File(Path.Combine(logsDir, "context-.log"), rollingInterval:
    /// RollingInterval.Day, ...)</c>). Finding the file by most-recent-write rather than assuming
    /// today's date means this keeps working correctly even if the host has been up since before
    /// midnight, when Serilog itself will have already rolled onto a new dated file.
    /// </summary>
    private static async Task<IResult> TailLogAsync(int? lines, ContextHostOptions hostOptions, CancellationToken ct)
    {
        var effectiveLines = lines is > 0 ? Math.Min(lines.Value, MaxTailLines) : DefaultTailLines;

        var logsDir = Path.Combine(hostOptions.ProjectDataDir, "logs");
        var latestLogFile = Directory.Exists(logsDir)
            ? new DirectoryInfo(logsDir).GetFiles("context-*.log").MaxBy(f => f.LastWriteTimeUtc)
            : null;

        if (latestLogFile is null)
        {
            return Results.Ok(new LogTailDto([], null));
        }

        try
        {
            // Serilog keeps the file open for writing while this host is running; FileShare.ReadWrite
            // is required so a concurrent tail request doesn't fail with a sharing violation.
            using var stream = new FileStream(
                latestLogFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var allLines = new List<string>();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                allLines.Add(line);
            }

            return Results.Ok(new LogTailDto(allLines.TakeLast(effectiveLines).ToArray(), latestLogFile.FullName));
        }
        catch (IOException)
        {
            return Results.Ok(new LogTailDto([], latestLogFile.FullName));
        }
    }
}

/// <summary>
/// Deliberately omits <see cref="Job.PayloadJson"/> — it can be arbitrarily large (a full file's
/// content for an indexing job) and isn't needed to see "what failed and why".
/// </summary>
public sealed record JobDto(
    Guid Id,
    JobType Type,
    Guid? ProjectId,
    JobStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? NextRetryAt)
{
    public static JobDto FromEntity(Job job) => new(
        job.Id, job.Type, job.ProjectId, job.Status, job.Attempts, job.LastError,
        job.CreatedAt, job.StartedAt, job.FinishedAt, job.NextRetryAt);
}

public sealed record LogTailDto(string[] Lines, string? FilePath);
