using Context.Application;
using Context.Application.Providers;
using Context.Claude;
using Context.Graph;
using Context.Host;
using Context.Host.Api;
using Context.Host.BackgroundServices;
using Context.Host.Hooks;
using Context.Host.Hubs;
using Context.Host.Security;
using Context.Indexing;
using Context.Infrastructure;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Context.Retrieval.Scoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var startupConfiguration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var hostOptions = ContextHostOptions.FromEnvironment(startupConfiguration);
Directory.CreateDirectory(hostOptions.ProjectDataDir);
var logsDir = Path.Combine(hostOptions.ProjectDataDir, "logs");
Directory.CreateDirectory(logsDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logsDir, "context-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information(
        "Starting Context.Host — project root {ProjectRoot}, bind {BindHost}:{Port}",
        hostOptions.ProjectRoot, hostOptions.BindHost, hostOptions.Port);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Services.AddSingleton(typeof(HostStartTime), new HostStartTime(DateTimeOffset.UtcNow));

    builder.WebHost.UseUrls($"http://{hostOptions.BindHost}:{hostOptions.Port}");

    builder.Host.UseSerilog();

    builder.Host.UseWindowsService(o => o.ServiceName = "ContextPlatformHost");
    builder.Host.UseSystemd();

    builder.Services.AddSingleton(hostOptions);
    builder.Services.AddSingleton<HostProjectContext>();
    builder.Services.AddSingleton<ClaudeSessionMap>();

    builder.Services.AddContextInfrastructure(hostOptions.DbPath);
    builder.Services.AddContextIndexing();
    builder.Services.AddContextGraph();

    builder.Services.Configure<HybridScoringOptions>(builder.Configuration.GetSection("ContextPlatform:Retrieval:Scoring"));
    builder.Services.Configure<HybridSearchOptions>(builder.Configuration.GetSection("ContextPlatform:Retrieval:Search"));
    builder.Services.Configure<ContextPackOptions>(builder.Configuration.GetSection("ContextPlatform:Retrieval:ContextPack"));
    builder.Services.AddContextRetrieval();

    builder.Services.AddContextMemory();

    var providersOptions = ContextProvidersOptionsResolver.Resolve(builder.Configuration, hostOptions.ProjectRoot);
    builder.Services.AddContextProviders(providersOptions);
    builder.Services.AddSingleton(Options.Create(providersOptions));

    builder.Services.AddContextClaude(o => o.HostPort = hostOptions.Port);
    builder.Services.AddContextApplication(hostOptions.ProjectRoot, hostOptions.ProjectDataDir);

    builder.Services.AddHostedService<FileWatcherHostedService>();

    builder.Services.AddOpenApi();

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton<IndexingHubNotifier>();

    builder.Services.AddSingleton<Context.Application.Indexing.IIndexingProgressReporter, Context.Host.Hubs.SignalRIndexingProgressReporter>();
    builder.Services.AddSingleton<Context.Application.Notifications.IProjectActivityNotifier, Context.Host.Hubs.SignalRProjectActivityNotifier>();

    // Dev-only, explicit allow-list — see BACKLOG-01 in docs/architecture/ARCHITECTURE_ASSESSMENT.md.
    // The previous SetIsOriginAllowed(_ => true).AllowCredentials() combination let ANY local
    // browser tab silently call this API/hub/MCP endpoint with the user's own cookies/credentials
    // implied — wildcard-origin + credentials is exactly the pattern browsers otherwise forbid for
    // good reason. Production registers no CORS policy at all: the production Dashboard is served
    // same-origin from this host's own wwwroot (see the static-file block below), so it never needs
    // one, and omitting the policy entirely (rather than an empty allow-list) means UseCors is never
    // even called in Production - no Access-Control-Allow-Origin header can be emitted for any
    // request, from any origin, no matter what.
    const string DashboardCorsPolicy = "Dashboard";
    string[] devCorsOrigins = ["http://localhost:4200", "https://localhost:4200"];
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddCors(o => o.AddPolicy(DashboardCorsPolicy, p => p
            .WithOrigins(devCorsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));
    }

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    await using (var migrationScope = app.Services.CreateAsyncScope())
    {
        var db = migrationScope.ServiceProvider.GetRequiredService<ContextDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    await HostProjectContextInitializer.InitializeAsync(app.Services, hostOptions.ProjectRoot).ConfigureAwait(false);

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseCors(DashboardCorsPolicy);
    }

    // Gates /hubs/indexing before routing selects an endpoint — see that method's remarks for why
    // this is middleware rather than an endpoint filter like every REST route below uses.
    app.UseHubBearerTokenAuth("/hubs/indexing");

    // Every group below applies BearerTokenAuth.RequireBearerTokenFilter to its own routes
    // internally (see each file's MapXxxEndpoints) — MapHealthEndpoints and MapBootstrapEndpoints
    // are the deliberate exceptions.
    app.MapHookEndpoints();
    app.MapProjectEndpoints();
    app.MapSearchEndpoints();
    app.MapMemoryEndpoints();
    app.MapGraphEndpoints();
    app.MapSessionEndpoints();
    app.MapHealthEndpoints();
    app.MapBootstrapEndpoints();
    app.MapSettingsEndpoints();
    app.MapClaudeEndpoints();
    app.MapSystemEndpoints();
    app.MapJobsEndpoints();
    app.MapFileSystemEndpoints();
    app.MapHub<IndexingHub>("/hubs/indexing");

    // ClaudeIntegrationInstaller writes .mcp.json as {"type":"http","url":"http://127.0.0.1:{port}/mcp"},
    // matching this route — no separate stdio process is spawned.
    app.MapMcp("/mcp").RequireBearerToken();

    // The Angular Dashboard's production build (apps/dashboard, published into wwwroot by the
    // Dockerfile's dashboard-build stage) is served from this same origin so its environment.ts
    // apiBaseUrl:'' (same-origin) resolves correctly with zero extra configuration. A bare-metal
    // `dotnet run` without that wwwroot present just serves nothing at "/" — use `ng serve`
    // separately for local Dashboard development instead (see apps/dashboard's own dev setup).
    if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapFallbackToFile("index.html");
    }

    await app.RunAsync().ConfigureAwait(false);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Context.Host terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Exposes the top-level-statement Program to WebApplicationFactory<Program> in Context.IntegrationTests.
public partial class Program;

// Captured once at startup so /api/system/status can report process uptime (CONTRACTS.md §12).
public readonly record struct HostStartTime(DateTimeOffset Value);
