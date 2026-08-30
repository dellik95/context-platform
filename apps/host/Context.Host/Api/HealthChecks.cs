using Context.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Context.Host.Api;

/// <summary>
/// Shared liveness checks used by both <see cref="HealthEndpoints"/> and <see cref="SystemEndpoints"/>
/// so the two surfaces can't drift on what "database reachable" means.
/// </summary>
internal static class HealthChecks
{
    public static async Task<bool> CheckDatabaseAsync(IDbContextFactory<ContextDbContext> dbFactory, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
