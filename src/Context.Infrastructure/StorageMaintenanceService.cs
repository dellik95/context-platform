using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure;

public sealed class StorageMaintenanceService(IDbContextFactory<ContextDbContext> dbFactory) : IStorageMaintenanceService
{
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CheckDatabaseAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task BackupToAsync(string destinationPath, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Parameterized via FormattableString -> ExecuteSqlAsync so the destination path is never
        // string-concatenated into the SQL text.
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {destinationPath}", ct).ConfigureAwait(false);
    }
}
