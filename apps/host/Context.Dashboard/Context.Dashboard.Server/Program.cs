using Context.Dashboard.Server.Startup;
using Serilog;

HostBootstrap.RaiseThreadPoolFloor();

var startupConfiguration = HostBootstrap.BuildStartupConfiguration();
var (hostOptions, migratedFromLegacyPath) = HostBootstrap.PrepareEnvironment(startupConfiguration);
HostBootstrap.ConfigureSerilog(hostOptions.ProjectDataDir);

try
{
    Log.Information(
        "Starting Context.Dashboard.Server — project root {ProjectRoot}, data dir {ProjectDataDir}, bind {BindHost}:{Port}",
        hostOptions.ProjectRoot, hostOptions.ProjectDataDir, hostOptions.BindHost, hostOptions.Port);

    if (migratedFromLegacyPath is not null)
    {
        Log.Information(
            "Copied existing database from legacy location {LegacyDbPath} to {DbPath}",
            migratedFromLegacyPath, hostOptions.DbPath);
    }

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.AddContextDashboardHost(hostOptions);

    var app = builder.Build();
    await app.InitializeStorageAsync().ConfigureAwait(false);
    app.ConfigureAndMapDashboardHost(hostOptions);

    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Context.Dashboard.Server terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Exposes the top-level-statement Program to WebApplicationFactory<Program> in Context.IntegrationTests.
public partial class Program;
