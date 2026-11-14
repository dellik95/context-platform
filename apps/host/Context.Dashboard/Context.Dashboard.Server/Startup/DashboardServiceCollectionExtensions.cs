using Context.Application;
using Context.Application.Jobs;
using Context.Application.Providers;
using Context.Claude;
using Context.Dashboard.Server.BackgroundServices;
using Context.Dashboard.Server.Hubs;
using Context.Dashboard.Server.Jobs;
using Context.Dashboard.Server.Mcp;
using Context.Dashboard.Server.Security;
using Context.Graph;
using Context.Indexing;
using Context.Infrastructure;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Context.Retrieval.Scoring;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using Serilog;
using StackExchange.Redis;

namespace Context.Dashboard.Server.Startup;

/// <summary>
/// Registers Context Platform domain, infrastructure, dashboard SignalR/job notifications,
/// CORS, and MCP server dependencies on <see cref="WebApplicationBuilder"/>.
/// </summary>
internal static class DashboardServiceCollectionExtensions
{
    public const string DashboardCorsPolicy = "Dashboard";

    public static WebApplicationBuilder AddContextDashboardHost(
        this WebApplicationBuilder builder,
        ContextHostOptions hostOptions)
    {
        builder.Services.AddSingleton(typeof(HostStartTime), new HostStartTime(DateTimeOffset.UtcNow));
        builder.WebHost.UseUrls($"http://{hostOptions.BindHost}:{hostOptions.Port}");

        builder.Host.UseSerilog();
        builder.Host.UseWindowsService(o => o.ServiceName = "ContextPlatformHost");
        builder.Host.UseSystemd();

        builder.Services.Configure<HostFilteringOptions>(o =>
        {
            o.AllowedHosts = [.. hostOptions.AllowedHosts];
            o.AllowEmptyHosts = true;
        });

        builder.Services.AddSingleton(hostOptions);
        builder.Services.AddSingleton<AdminTokenProvider>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<CurrentProjectAccessor>();

        builder.AddPlatformSubsystems(hostOptions);
        builder.AddRealtimeAndBackgroundServices(hostOptions);
        builder.AddApiAndMcpServices();

        return builder;
    }

    private static void AddPlatformSubsystems(this WebApplicationBuilder builder, ContextHostOptions hostOptions)
    {
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
    }

    private static void AddRealtimeAndBackgroundServices(this WebApplicationBuilder builder, ContextHostOptions hostOptions)
    {
        builder.Services.Configure<FileWatchingOptions>(builder.Configuration.GetSection("ContextPlatform:FileWatching"));
        builder.Services.AddSingleton<ProjectFileWatcherManager>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProjectFileWatcherManager>());

        builder.Services.Configure<JobDispatcherOptions>(o =>
        {
            if (int.TryParse(builder.Configuration["CONTEXT_PLATFORM_JOB_WORKERS_PER_PROJECT"], out var workersPerProject)
                && workersPerProject > 0)
            {
                o.WorkersPerProject = workersPerProject;
            }

            if (int.TryParse(builder.Configuration["CONTEXT_PLATFORM_JOB_SHARED_WORKERS"], out var sharedWorkers)
                && sharedWorkers >= 0)
            {
                o.SharedWorkers = sharedWorkers;
            }
        });

        builder.Services.AddSignalR()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.AddSingleton<IndexingHubNotifier>();

        builder.Services.AddSingleton<Context.Application.Indexing.IIndexingProgressReporter, SignalRIndexingProgressReporter>();
        builder.Services.AddSingleton<Context.Application.Notifications.IProjectActivityNotifier, SignalRProjectActivityNotifier>();
        builder.Services.AddSingleton<Context.Application.Notifications.IJobCountsNotifier, SignalRJobCountsNotifier>();
        builder.Services.AddSingleton<Context.Application.Notifications.IHookInvocationNotifier, SignalRHookInvocationNotifier>();
        builder.Services.AddScoped<Context.Application.Indexing.IProjectStatusPublisher, SignalRProjectStatusPublisher>();

        if (hostOptions.RedisConnectionString is { Length: > 0 } redisConnectionString)
        {
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
                redisOptions.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            builder.Services.AddSingleton<IJobSignal, RedisJobSignal>();
        }
    }

    private static void AddApiAndMcpServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        if (builder.Environment.IsDevelopment())
        {
            string[] devCorsOrigins = ["http://localhost:4200", "https://localhost:4200"];
            builder.Services.AddCors(o => o.AddPolicy(DashboardCorsPolicy, p => p
                .WithOrigins(devCorsOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
        }

        builder.Services.AddScoped<McpNodeResolver>();
        builder.Services.AddScoped<McpSavingsTracker>();

        // Stateless HTTP transport ensures each tool call executes inside an active HttpContext
        // so RequireBearerTokenFilter and CurrentProjectAccessor resolve the project reliably.
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly();
    }
}
