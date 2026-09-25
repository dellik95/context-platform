using Context.Application;
using Context.Application.Providers;
using Context.Claude;
using Context.Graph;
using Context.Dashboard.Server;
using Context.Dashboard.Server.Api;
using Context.Dashboard.Server.BackgroundServices;
using Context.Dashboard.Server.Hooks;
using Context.Dashboard.Server.Hubs;
using Context.Dashboard.Server.Security;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Context.Retrieval.Scoring;
using Context.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// Raise the thread-pool floor before anything schedules work. Microsoft.Data.Sqlite has no true
// async path — every awaited DB call blocks the pool thread it runs on for the duration of the
// file I/O — and the indexing pipeline reads source files off whatever (possibly slow, e.g. a WSL
// /mnt/c drvfs mount) filesystem the project lives on. Past the floor the pool only injects one or
// two threads per second, so a burst of blocking work starves unrelated *non*-blocking callbacks
// for seconds: the symptom is a StackExchange.Redis RedisTimeoutException whose counters show the
// response already sat unread in the socket (`in:` non-zero) while WORKER Busy exceeded Min.
{
    ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
    ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, 128), Math.Max(minCompletionPortThreads, 128));
}

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

// One-time carry-forward: a repo previously run against an older build may still have a database
// at the old {ProjectRoot}/.context/context.db location — copy it into the new shared data dir
// (never move: the old file is left in place as a safety net) so it doesn't appear to vanish.
var migratedFromLegacyPath = SharedDataLocation.EnsureMigratedFromLegacyLocation(hostOptions.DbPath, hostOptions.ProjectRoot);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Hosting.Diagnostics' "Request starting" lines include the query string, which for SignalR
    // carries the project bearer token as ?access_token=. EF's per-command SQL is pure noise at Info.
    .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
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

    builder.Services.AddSingleton(typeof(HostStartTime), new HostStartTime(DateTimeOffset.UtcNow));

    builder.WebHost.UseUrls($"http://{hostOptions.BindHost}:{hostOptions.Port}");

    builder.Host.UseSerilog();

    builder.Host.UseWindowsService(o => o.ServiceName = "ContextPlatformHost");
    builder.Host.UseSystemd();

    builder.Services.Configure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(o =>
    {
        o.AllowedHosts = [.. hostOptions.AllowedHosts];
        // Kept at the framework default: a missing/empty Host header is an HTTP/1.0 or raw-socket
        // client, never a browser, so it is not a rebinding vector — and the hook scripts' curl
        // calls must keep working.
        o.AllowEmptyHosts = true;
    });

    builder.Services.AddSingleton(hostOptions);
    builder.Services.AddSingleton<ClaudeSessionMap>();
    builder.Services.AddSingleton<AdminTokenProvider>();

    // CurrentProjectAccessor reads the Project that BearerTokenAuth/HubBearerTokenAuthMiddleware
    // stashed into HttpContext.Items for this request — needs IHttpContextAccessor to get at that
    // HttpContext from anywhere else in the DI graph.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<CurrentProjectAccessor>();

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

    // ProjectFileWatcherManager owns one FileSystemWatcher per registered Project (see its own
    // remarks) — registered as both the singleton ProjectEndpoints resolves to start watching a
    // brand-new project immediately (and stop watching one on delete), and the IHostedService that
    // watches every already-registered project from host startup and disposes them all on
    // shutdown. Same dual-role pattern JobDispatcher uses for itself
    // (Context.Application.ServiceCollectionExtensions.AddJobDispatcher).
    builder.Services.AddSingleton<ProjectFileWatcherManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ProjectFileWatcherManager>());

    builder.Services.AddOpenApi();

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton<IndexingHubNotifier>();

    if (int.TryParse(builder.Configuration["CONTEXT_PLATFORM_JOB_WORKERS_PER_PROJECT"], out var jobWorkersPerProject)
        && jobWorkersPerProject > 0)
    {
        builder.Services.Configure<Context.Application.Jobs.JobDispatcherOptions>(o => o.WorkersPerProject = jobWorkersPerProject);
    }

    builder.Services.AddSingleton<Context.Application.Indexing.IIndexingProgressReporter, Context.Dashboard.Server.Hubs.SignalRIndexingProgressReporter>();
    builder.Services.AddSingleton<Context.Application.Notifications.IProjectActivityNotifier, Context.Dashboard.Server.Hubs.SignalRProjectActivityNotifier>();
    builder.Services.AddSingleton<Context.Application.Notifications.IJobCountsNotifier, Context.Dashboard.Server.Hubs.SignalRJobCountsNotifier>();
    builder.Services.AddSingleton<Context.Application.Notifications.IHookInvocationNotifier, Context.Dashboard.Server.Hubs.SignalRHookInvocationNotifier>();

    // Multi-instance job dispatch: overrides Context.Application's single-process InMemoryJobSignal
    // default only when CONTEXT_PLATFORM_REDIS is configured, so more than one Context.Dashboard.Server
    // instance can share one job queue (each still claims work from the same database via
    // IJobRepository.DequeueNextAsync — Redis only relays the "go check again" wake-up). Unset
    // (the common single-instance case) leaves today's in-memory behavior untouched.
    if (hostOptions.RedisConnectionString is { Length: > 0 } redisConnectionString)
    {
        builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        {
            var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnectionString);
            // Redis being briefly down/not-yet-started must never crash this host at startup (same
            // fail-open posture as everything else Ollama/hooks-adjacent) -- without this,
            // ConnectionMultiplexer.Connect throws on a failed first attempt instead of retrying in
            // the background, and job dispatch would simply fall back to no signal at all (the
            // JobDispatcher's own poll-timer fallback still keeps jobs moving, just up to
            // PollInterval slower) rather than take the whole process down with it.
            redisOptions.AbortOnConnectFail = false;
            return StackExchange.Redis.ConnectionMultiplexer.Connect(redisOptions);
        });
        builder.Services.AddSingleton<Context.Application.Jobs.IJobSignal, Context.Dashboard.Server.Jobs.RedisJobSignal>();
    }

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

    // Stateless: tool-call dispatch (RunSessionAsync) runs outside the HTTP request that carried
    // the JSON-RPC message, so IHttpContextAccessor.HttpContext is null by the time a tool method
    // runs — CurrentProjectAccessor would throw "used outside an HTTP request" for every tool that
    // reads it. Stateless mode round-trips session state via the mcp-session-id header instead of
    // in-memory, so each request (including tool calls) carries its own HttpContext through
    // RequireBearerTokenFilter. Costs: no /sse endpoint, no server-to-client requests/sampling/roots
    // — none of which this project's tools use.
    builder.Services.AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly();

    var app = builder.Build();

    await using (var migrationScope = app.Services.CreateAsyncScope())
    {
        var db = migrationScope.ServiceProvider.GetRequiredService<ContextDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);

        // One-time reconciliation for projects installed before Phase 3's shared-host bearer-token
        // model: backfills Project.BearerTokenHash from whatever token is already sitting in each
        // project's own .claude/context-platform.local.json, so an existing install keeps
        // authenticating without needing a re-install. See ProjectTokenBackfill's own remarks for
        // why this runs once here rather than lazily inside BearerTokenAuth's lookup path.
        var projectsForBackfill = migrationScope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await ProjectTokenBackfill.ReconcileAsync(projectsForBackfill, CancellationToken.None).ConfigureAwait(false);
    }

    // First in the pipeline, before anything can act on the request: rejects Host headers outside
    // ContextHostOptions.AllowedHosts, which is what stops a DNS-rebinding page from driving this
    // loopback-bound API from a victim's browser. Skipped when the allow-list is "*" (a container
    // bind that has not been told its own name) so the middleware isn't paid for nothing.
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
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseCors(DashboardCorsPolicy);
    }

    // Gates /hubs/indexing before routing selects an endpoint — see that method's remarks for why
    // this is middleware rather than an endpoint filter like every REST route below uses.
    app.UseHubBearerTokenAuth("/hubs/indexing");

    // Every group below applies BearerTokenAuth.RequireBearerTokenFilter (or, for project
    // creation/listing/fs-browse, AdminTokenAuth) to its own routes internally (see each file's
    // MapXxxEndpoints) — MapHealthEndpoints and MapAdminTokenBootstrapEndpoints are the deliberate
    // exceptions (the latter is loopback-gated in its own handler instead).
    app.MapAdminTokenBootstrapEndpoints();
    app.MapHookEndpoints();
    app.MapProjectEndpoints();
    app.MapSearchEndpoints();
    app.MapMemoryEndpoints();
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

    // ClaudeIntegrationInstaller writes .mcp.json as {"type":"http","url":"http://127.0.0.1:{port}/mcp"},
    // matching this route — no separate stdio process is spawned.
    app.MapMcp("/mcp").RequireBearerToken();

    // The Angular Dashboard's production build (apps/host/Context.Dashboard/context.dashboard.client,
    // published into wwwroot by the Dockerfile's dashboard-build stage) is served from this same
    // origin so its environment.ts apiBaseUrl:'' (same-origin) resolves correctly with zero extra
    // configuration. A bare-metal `dotnet run` without that wwwroot present just serves nothing at
    // "/" — use `ng serve` separately for local Dashboard development instead (see
    // context.dashboard.client's own dev setup).
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
    Log.Fatal(ex, "Context.Dashboard.Server terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Exposes the top-level-statement Program to WebApplicationFactory<Program> in Context.IntegrationTests.
public partial class Program;

// Captured once at startup so /api/system/status can report process uptime (CONTRACTS.md §12).
public readonly record struct HostStartTime(DateTimeOffset Value);
