namespace Context.Infrastructure.Abstractions;

/// <summary>
/// Encapsulates schema migration, liveness probing, and online SQLite backup operations inside
/// <c>Context.Infrastructure</c> so higher layers do not couple directly to <c>ContextDbContext</c>.
/// </summary>
public interface IStorageMaintenanceService
{
    Task MigrateAsync(CancellationToken ct = default);

    Task<bool> CheckDatabaseAsync(CancellationToken ct = default);

    Task BackupToAsync(string destinationPath, CancellationToken ct = default);
}
