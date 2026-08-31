using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;

namespace Context.Host.Api;

/// <summary>
/// <c>/api/jobs</c> and <c>/api/logs/tail</c> — operational visibility for the Dashboard's System
/// tab: which jobs failed and why, a manual re-trigger for a permanently
/// <see cref="JobStatus.Failed"/> job, and a tail of this host's own log file. All three are the
/// read/act surface over what <see cref="JobDispatcher"/> and Serilog already track — no new
/// tracking is introduced here.
/// </summary>
public static class JobsEndpoints
{
    private const int DefaultListLimit = 50;
    private const int DefaultTailLines = 200;
    private const int MaxTailLines = 5000;

    public static void MapJobsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/jobs", ListAsync);
        app.MapPost("/api/jobs/{id:guid}/retry", RetryAsync);
        app.MapGet("/api/logs/tail", TailLogAsync);
    }

    private static async Task<IResult> ListAsync(
        JobStatus? status, int? limit, IJobRepository jobs, CancellationToken ct)
    {
        // Failed is the primary use case this feature exists for (finding/retrying broken jobs),
        // so it's the default when the caller doesn't ask for a specific status.
        var effectiveStatus = status ?? JobStatus.Failed;
        var effectiveLimit = limit is > 0 ? limit.Value : DefaultListLimit;

        var results = await jobs.ListByStatusAsync(effectiveStatus, ct).ConfigureAwait(false);
        return Results.Ok(results.Take(effectiveLimit).Select(JobDto.FromEntity));
    }

    private static async Task<IResult> RetryAsync(Guid id, IJobQueue jobQueue, CancellationToken ct)
    {
        try
        {
            await jobQueue.RequeueAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (JobNotFoundException)
        {
            return Results.NotFound(new { message = $"No job with id '{id}' exists." });
        }
        catch (JobNotRetryableException ex)
        {
            return Results.Conflict(new { message = $"Job '{id}' is not retryable: it is currently {ex.ActualStatus}, not {JobStatus.Failed}." });
        }
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
    JobStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? NextRetryAt)
{
    public static JobDto FromEntity(Job job) => new(
        job.Id, job.Type, job.Status, job.Attempts, job.LastError,
        job.CreatedAt, job.StartedAt, job.FinishedAt, job.NextRetryAt);
}

public sealed record LogTailDto(string[] Lines, string? FilePath);
