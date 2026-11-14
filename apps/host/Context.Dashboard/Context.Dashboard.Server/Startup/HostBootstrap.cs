using Context.Shared;
using Serilog;
using Serilog.Events;

namespace Context.Dashboard.Server.Startup;

/// <summary>
/// Pre-builder host bootstrap helpers: thread-pool floor sizing, startup configuration loading,
/// shared data-directory migration, and Serilog initialization.
/// </summary>
internal static class HostBootstrap
{
    /// <summary>
    /// Raises the thread-pool floor before scheduling work so synchronous SQLite/filesystem I/O does
    /// not starve unrelated async callbacks (such as Redis heartbeats).
    /// </summary>
    public static void RaiseThreadPoolFloor(int minThreads = 128)
    {
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, minThreads), Math.Max(minCompletionPortThreads, minThreads));
    }

    public static IConfigurationRoot BuildStartupConfiguration() =>
        new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

    public static (ContextHostOptions Options, string? MigratedFromLegacyPath) PrepareEnvironment(IConfiguration configuration)
    {
        var hostOptions = ContextHostOptions.FromEnvironment(configuration);
        Directory.CreateDirectory(hostOptions.ProjectDataDir);
        Directory.CreateDirectory(Path.Combine(hostOptions.ProjectDataDir, "logs"));

        // One-time carry-forward from legacy {ProjectRoot}/.context/context.db (copy only).
        var migratedFromLegacyPath = SharedDataLocation.EnsureMigratedFromLegacyLocation(hostOptions.DbPath, hostOptions.ProjectRoot);
        return (hostOptions, migratedFromLegacyPath);
    }

    public static void ConfigureSerilog(string projectDataDir)
    {
        var logsDir = Path.Combine(projectDataDir, "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            // Hosting.Diagnostics logs raw query strings (carrying SignalR ?access_token=); EF SQL is Info noise.
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(logsDir, "context-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
    }
}
