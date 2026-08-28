using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Backs up the live SQLite database via SQLite's own <c>VACUUM INTO</c> (an atomic, consistent,
/// online snapshot that doesn't block concurrent readers/writers) into a timestamped file under
/// <c>{ProjectDataDir}/backups/</c>, per the task brief §2.
/// </summary>
public sealed class BackupDbJobHandler(ContextDbContext db, ContextApplicationOptions options) : IJobHandler
{
    public JobType Type => JobType.BackupDb;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var backupsDir = Path.Combine(options.ProjectDataDir, "backups");
        Directory.CreateDirectory(backupsDir);

        var backupPath = Path.Combine(backupsDir, $"context-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");

        // Parameterized via FormattableString -> ExecuteSqlInterpolatedAsync so the path is never
        // string-concatenated into the SQL text, even though it's a process-generated path here.
        await db.Database.ExecuteSqlInterpolatedAsync($"VACUUM INTO {backupPath}", ct).ConfigureAwait(false);
    }
}
