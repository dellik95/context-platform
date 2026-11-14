using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Backs up the live SQLite database via SQLite's own <c>VACUUM INTO</c> (an atomic, consistent,
/// online snapshot that doesn't block concurrent readers/writers) into a timestamped file under
/// <c>{ProjectDataDir}/backups/</c>, per the task brief §2.
/// </summary>
public sealed class BackupDbJobHandler(
    IStorageMaintenanceService storageMaintenance,
    ContextApplicationOptions options,
    ILogger<BackupDbJobHandler>? logger = null) : IJobHandler
{
    private const string BackupPattern = "context-*.db";

    /// <summary>
    /// How many snapshots survive a run. Each one is a full copy of the database, so an unbounded
    /// series of daily backups fills the disk with the very data it exists to protect.
    /// </summary>
    private const int KeepBackups = 7;

    public JobType Type => JobType.BackupDb;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var backupsDir = Path.Combine(options.ProjectDataDir, "backups");
        Directory.CreateDirectory(backupsDir);

        var backupPath = Path.Combine(backupsDir, $"context-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");
        await storageMaintenance.BackupToAsync(backupPath, ct).ConfigureAwait(false);

        PruneOldBackups(backupsDir);
    }

    private void PruneOldBackups(string backupsDir)
    {
        // Ordered by the timestamp in the name rather than by write time: the names sort
        // lexicographically in chronological order, and a file copy or restore would rewrite the
        // mtime of an old snapshot and make it look like the newest one.
        var stale = Directory.EnumerateFiles(backupsDir, BackupPattern)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(KeepBackups);

        foreach (var path in stale)
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A snapshot that can't be deleted is not a reason to fail the backup that succeeded.
                logger?.LogWarning(ex, "Could not delete the expired database backup '{BackupPath}'.", path);
            }
        }
    }
}
