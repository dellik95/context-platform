using Context.Application.Jobs.Handlers;
using Context.Dashboard.Server.Api;
using Context.Dashboard.Server.Hooks;
using Context.Dashboard.Server.Hubs;
using Context.Dashboard.Server.Security;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Context.Dashboard.Server.Startup;

/// <summary>
/// Database initialization, middleware pipeline configuration, REST/SignalR/MCP endpoint mapping,
/// and production Angular SPA static file fallback.
/// </summary>
internal static class DashboardApplicationExtensions
{
    public static async Task InitializeStorageAsync(this WebApplication app)
    {
        await using var migrationScope = app.Services.CreateAsyncScope();
        var db = migrationScope.ServiceProvider.GetRequiredService<ContextDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);

        var projectsForBackfill = migrationScope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await ProjectTokenBackfill.ReconcileAsync(projectsForBackfill, CancellationToken.None).ConfigureAwait(false);

        var orphanedApplies = await ReconsolidateMemoryJobHandler.FailOrphanedAppliesAsync(
            migrationScope.ServiceProvider.GetRequiredService<IMemoryRepository>(),
            migrationScope.ServiceProvider.GetRequiredService<IJobRepository>(),
            CancellationToken.None).ConfigureAwait(false);
        if (orphanedApplies > 0)
        {
            Log.Warning("Marked {Count} reconsolidation report(s) Failed: left Applying with no job to resume them.", orphanedApplies);
        }
    }

    public static WebApplication ConfigureAndMapDashboardHost(this WebApplication app, ContextHostOptions hostOptions)
    {
        if (!hostOptions.AllowedHosts.Contains("*"))
        {
            app.UseHostFiltering();
        }
        else
        {
            Log.Warning(
                "Host filtering is disabled (allowed hosts '*') for bind address {BindHost} — set CONTEXT_PLATFORM_ALLOWED_HOSTS to the names this host is reached by.",
                hostOptions.BindHost);
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseCors(DashboardServiceCollectionExtensions.DashboardCorsPolicy);
        }

        app.UseHubBearerTokenAuth("/hubs/indexing");

        app.MapAdminTokenBootstrapEndpoints();
        app.MapHookEndpoints();
        app.MapProjectEndpoints();
        app.MapSearchEndpoints();
        app.MapMemoryEndpoints();
        app.MapMemoryReconsolidationEndpoints();
        app.MapGraphEndpoints();
        app.MapSessionEndpoints();
        app.MapHealthEndpoints();
        app.MapSettingsEndpoints();
        app.MapClaudeEndpoints();
        app.MapSystemEndpoints();
        app.MapJobsEndpoints();
        app.MapHooksStatusEndpoints();
        app.MapUsageEndpoints();
        app.MapFileSystemEndpoints();
        app.MapHub<IndexingHub>("/hubs/indexing");
        app.MapMcp("/mcp").RequireBearerToken();

        if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("index.html");
        }

        return app;
    }
}
