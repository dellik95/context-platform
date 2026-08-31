using System.Text.Json;
using System.Text.Json.Nodes;
using Context.Application;
using Context.Application.Settings;
using Context.Claude;
using Context.Domain.Entities;
using Context.Graph;
using Context.Host;
using Context.Host.Api;
using Context.Host.BackgroundServices;
using Context.Host.Hooks;
using Context.Host.Hubs;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Microsoft.EntityFrameworkCore;
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
    var retrievalScoringOptions = BuildHybridScoringOptions(builder.Configuration);
    var retrievalSearchOptions = BuildHybridSearchOptions(builder.Configuration);
    var retrievalPackOptions = BuildContextPackOptions(builder.Configuration);
    builder.Services.AddContextRetrieval(retrievalScoringOptions, retrievalSearchOptions, retrievalPackOptions);
    builder.Services.AddContextMemory();
    // The Settings tab's saved .claude/context-platform.local.json overrides CONTEXT_* env vars -
    // this is what "Provider changes require a Context.Host restart" in the Dashboard refers to.
    var localProjectSettings = await TryReadLocalProjectSettingsAsync(hostOptions.ProjectRoot).ConfigureAwait(false);
    var providersOptions = BuildProvidersOptions(builder.Configuration, localProjectSettings);
    builder.Services.AddContextProviders(providersOptions);

    builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(providersOptions));
    builder.Services.AddContextClaude(o => o.HostPort = hostOptions.Port);

    builder.Services.AddContextApplication(hostOptions.ProjectDataDir);

    builder.Services.AddHostedService<FileWatcherHostedService>();

    builder.Services.AddOpenApi();

    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton<IndexingHubNotifier>();

    builder.Services.AddSingleton<Context.Application.Indexing.IIndexingProgressReporter, Context.Host.Hubs.SignalRIndexingProgressReporter>();
    builder.Services.AddSingleton<Context.Application.Notifications.IProjectActivityNotifier, Context.Host.Hubs.SignalRProjectActivityNotifier>();

    const string DashboardCorsPolicy = "Dashboard";
    builder.Services.AddCors(o => o.AddPolicy(DashboardCorsPolicy, p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    await using (var migrationScope = app.Services.CreateAsyncScope())
    {
        var db = migrationScope.ServiceProvider.GetRequiredService<ContextDbContext>();
        await db.Database.MigrateAsync().ConfigureAwait(false);
    }

    await InitializeHostProjectContextAsync(app, hostOptions.ProjectRoot).ConfigureAwait(false);

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseCors(DashboardCorsPolicy);

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
    app.MapFileSystemEndpoints();
    app.MapHub<IndexingHub>("/hubs/indexing");

    // ClaudeIntegrationInstaller writes .mcp.json as {"type":"http","url":"http://127.0.0.1:{port}/mcp"},
    // matching this route — no separate stdio process is spawned.
    app.MapMcp("/mcp");

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

static Context.Retrieval.Scoring.HybridScoringOptions BuildHybridScoringOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("ContextPlatform:Retrieval:Scoring");

    return new Context.Retrieval.Scoring.HybridScoringOptions
    {
        LexicalWeight = double.TryParse(section["LexicalWeight"], out var lexical) ? lexical : Context.Retrieval.Scoring.HybridScorer.DefaultLexicalWeight,
        VectorWeight = double.TryParse(section["VectorWeight"], out var vector) ? vector : Context.Retrieval.Scoring.HybridScorer.DefaultVectorWeight,
        GraphWeight = double.TryParse(section["GraphWeight"], out var graph) ? graph : Context.Retrieval.Scoring.HybridScorer.DefaultGraphWeight,
        RecencyWeight = double.TryParse(section["RecencyWeight"], out var recency) ? recency : Context.Retrieval.Scoring.HybridScorer.DefaultRecencyWeight,
        MemoryWeight = double.TryParse(section["MemoryWeight"], out var memory) ? memory : Context.Retrieval.Scoring.HybridScorer.DefaultMemoryWeight,
    };
}

static Context.Retrieval.HybridSearchOptions BuildHybridSearchOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("ContextPlatform:Retrieval:Search");

    return new Context.Retrieval.HybridSearchOptions
    {
        GraphExpansionSeedCount = int.TryParse(section["GraphExpansionSeedCount"], out var seeds)
            ? seeds
            : Context.Retrieval.HybridSearchEngine.DefaultGraphExpansionSeedCount,
        GraphExpansionHops = int.TryParse(section["GraphExpansionHops"], out var hops)
            ? hops
            : Context.Retrieval.HybridSearchEngine.DefaultGraphExpansionHops,
        RerankTopK = int.TryParse(section["RerankTopK"], out var rerankTopK)
            ? rerankTopK
            : Context.Retrieval.HybridSearchEngine.DefaultRerankTopK,
        SemanticBoostEnabled = bool.TryParse(section["SemanticBoostEnabled"], out var semanticBoostEnabled)
            ? semanticBoostEnabled
            : true,
        SemanticStrongMatchThreshold = double.TryParse(section["SemanticStrongMatchThreshold"], out var semanticStrongMatchThreshold)
            ? semanticStrongMatchThreshold
            : Context.Retrieval.HybridSearchEngine.DefaultSemanticStrongMatchThreshold,
        SemanticBoostFactor = double.TryParse(section["SemanticBoostFactor"], out var semanticBoostFactor)
            ? semanticBoostFactor
            : Context.Retrieval.HybridSearchEngine.DefaultSemanticBoostFactor,
    };
}

static Context.Retrieval.ContextPackOptions BuildContextPackOptions(IConfiguration configuration)
{
    var section = configuration.GetSection("ContextPlatform:Retrieval:ContextPack");

    return new Context.Retrieval.ContextPackOptions
    {
        ArchitectureReserveRatio = double.TryParse(section["ArchitectureReserveRatio"], out var architectureReserve)
            ? architectureReserve
            : Context.Retrieval.ContextPackBuilder.DefaultArchitectureReserveRatio,
        PrimaryReserveRatio = double.TryParse(section["PrimaryReserveRatio"], out var primaryReserve)
            ? primaryReserve
            : Context.Retrieval.ContextPackBuilder.DefaultPrimaryReserveRatio,
        HistoricalReserveRatio = double.TryParse(section["HistoricalReserveRatio"], out var historicalReserve)
            ? historicalReserve
            : Context.Retrieval.ContextPackBuilder.DefaultHistoricalReserveRatio,
        OpenIssuesReserveRatio = double.TryParse(section["OpenIssuesReserveRatio"], out var openIssuesReserve)
            ? openIssuesReserve
            : Context.Retrieval.ContextPackBuilder.DefaultOpenIssuesReserveRatio,
        SafetyReserveRatio = double.TryParse(section["SafetyReserveRatio"], out var safetyReserve)
            ? safetyReserve
            : Context.Retrieval.ContextPackBuilder.DefaultSafetyReserveRatio,
    };
}

static async Task<ProjectSettingsDto?> TryReadLocalProjectSettingsAsync(string projectRoot)
{
    var path = Path.Combine(projectRoot, ".claude", "context-platform.local.json");
    if (!System.IO.File.Exists(path))
    {
        return null;
    }

    try
    {
        var json = await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(false);
        var root = JsonNode.Parse(json) as JsonObject;
        return root?["settings"]?.Deserialize<ProjectSettingsDto>();
    }
    catch (JsonException)
    {
        return null;
    }
    catch (IOException)
    {
        return null;
    }
}

static ContextProvidersOptions BuildProvidersOptions(IConfiguration configuration, ProjectSettingsDto? localProjectSettings)
{
    var openAiKey = configuration["CONTEXT_OPENAI_API_KEY"];
    var anthropicKey = configuration["CONTEXT_ANTHROPIC_API_KEY"];

    // A local Ollama install (CONTEXT_EMBEDDING_PROVIDER=ollama and/or CONTEXT_LLM_PROVIDER=ollama,
    // or the Dashboard's Settings tab, which wins over these env vars - see localProjectSettings
    // below) is the fully-offline, no-API-key upgrade path over the noop defaults: real embeddings
    // for search, and a real model for session summarization/memory extraction (Context.Memory)
    // that currently degrade to templated heuristics under noop. Base URL/model are overridable in
    // case Ollama isn't on localhost:11434 or a different model was pulled.
    var ollamaBaseUrl = configuration["CONTEXT_OLLAMA_BASE_URL"];
    var ollamaEmbeddingModel = configuration["CONTEXT_OLLAMA_EMBEDDING_MODEL"];
    var ollamaChatModel = configuration["CONTEXT_OLLAMA_CHAT_MODEL"];

    var effectiveOllamaBaseUrl = localProjectSettings?.OllamaBaseUrl is { Length: > 0 } localBaseUrl
        ? localBaseUrl
        : ollamaBaseUrl is { Length: > 0 } ? ollamaBaseUrl : "http://localhost:11434";

    return new ContextProvidersOptions
    {
        EmbeddingProvider = localProjectSettings?.EmbeddingProvider is { Length: > 0 } localEmbeddingProvider
            ? localEmbeddingProvider
            : configuration["CONTEXT_EMBEDDING_PROVIDER"] is { Length: > 0 } embedding ? embedding : "noop",
        LlmProvider = localProjectSettings?.LlmProvider is { Length: > 0 } localLlmProvider
            ? localLlmProvider
            : configuration["CONTEXT_LLM_PROVIDER"] is { Length: > 0 } llm ? llm : "noop",
        OpenAi = new Context.Providers.Embeddings.OpenAiOptions { ApiKey = openAiKey },
        Anthropic = new Context.Providers.Llm.AnthropicOptions { ApiKey = anthropicKey },
        Ollama = new Context.Providers.Embeddings.OllamaOptions
        {
            BaseUrl = effectiveOllamaBaseUrl,
            Model = localProjectSettings?.OllamaEmbeddingModel is { Length: > 0 } localEmbeddingModel
                ? localEmbeddingModel
                : ollamaEmbeddingModel is { Length: > 0 } ? ollamaEmbeddingModel : "embeddinggemma"
        },
        OllamaChat = new Context.Providers.Llm.OllamaChatOptions
        {
            BaseUrl = effectiveOllamaBaseUrl,
            Model = localProjectSettings?.OllamaChatModel is { Length: > 0 } localChatModel
                ? localChatModel
                : ollamaChatModel is { Length: > 0 } ? ollamaChatModel : Context.Providers.Llm.OllamaChatOptions.DefaultModel
        }
    };
}

/// <summary>
/// Resolves (creating if necessary) the single <see cref="Project"/> row this Context.Host process
/// serves, and populates <see cref="HostProjectContext"/> from it plus the bearer token in
/// <c>.claude/context-platform.local.json</c> (if that file exists — see
/// <see cref="IClaudeIntegrationInstaller"/>). Runs synchronously, once, right after the DI
/// container is built and before the host starts accepting requests or running any
/// <see cref="IHostedService"/> — every hook endpoint, the file watcher, and every MCP tool's
/// default <c>projectId</c> depend on this having already happened.
/// </summary>
static async Task InitializeHostProjectContextAsync(WebApplication app, string projectRoot)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Context.Host.Startup");
    var hostProject = app.Services.GetRequiredService<HostProjectContext>();

    var (configProjectId, bearerToken) = await TryReadLocalConfigAsync(projectRoot, logger).ConfigureAwait(false);

    using var scope = app.Services.CreateScope();
    var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

    // Look up by the id from .claude/context-platform.local.json FIRST, before falling back to
    // root-path matching: the same mounted repo (and thus the same local config file) can be
    // served from different effective root paths across environments — e.g. a bare-metal run
    // sees "C:\...\repo" while a container run of the identical repo sees "/workspace" — so a
    // root-path-only lookup would treat them as two different projects and then fail inserting a
    // second row with the SAME configProjectId (UNIQUE constraint on Projects.Id). Reusing the
    // existing row (and just updating its RootPath) keeps memories/sessions/index data attached
    // to the one logical project regardless of which environment last touched it.
    var project = configProjectId is { } id
        ? await projects.GetByIdAsync(id, CancellationToken.None).ConfigureAwait(false)
        : null;
    project ??= await projects.GetByRootPathAsync(projectRoot, CancellationToken.None).ConfigureAwait(false);

    if (project is not null && !string.Equals(project.RootPath, projectRoot, StringComparison.Ordinal))
    {
        logger.LogInformation(
            "Project {ProjectId} root path changed from '{OldRoot}' to '{NewRoot}' (different environment serving the same project).",
            project.Id, project.RootPath, projectRoot);
        project.RootPath = projectRoot;
        await projects.UpdateAsync(project, CancellationToken.None).ConfigureAwait(false);
    }

    if (project is null)
    {
        project = await projects.AddAsync(new Project
        {
            Id = configProjectId ?? Guid.NewGuid(),
            Name = new DirectoryInfo(projectRoot).Name,
            RootPath = projectRoot,
            CreatedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None).ConfigureAwait(false);

        logger.LogInformation("Created project {ProjectId} for root '{ProjectRoot}'.", project.Id, projectRoot);
    }

    hostProject.Initialize(project.Id, projectRoot, bearerToken);

    if (bearerToken is null)
    {
        logger.LogWarning(
            "No usable '.claude/context-platform.local.json' bearer token found under '{ProjectRoot}' — " +
            "/hooks/* endpoints will reject every request until `context claude install` is run for this project.",
            projectRoot);
    }

    logger.LogInformation("Context.Host is bound to project {ProjectId} at '{ProjectRoot}'.", project.Id, projectRoot);
}

static async Task<(Guid? ProjectId, string? BearerToken)> TryReadLocalConfigAsync(string projectRoot, Microsoft.Extensions.Logging.ILogger logger)
{
    var path = Path.Combine(projectRoot, ".claude", "context-platform.local.json");
    if (!System.IO.File.Exists(path))
    {
        return (null, null);
    }

    try
    {
        var json = await System.IO.File.ReadAllTextAsync(path).ConfigureAwait(false);
        var config = JsonSerializer.Deserialize<HookLocalConfig>(json);
        if (config is null || string.IsNullOrWhiteSpace(config.BearerToken))
        {
            return (null, null);
        }

        var projectId = Guid.TryParse(config.ProjectId, out var parsed) ? parsed : (Guid?)null;
        return (projectId, config.BearerToken);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to read/parse '{Path}'.", path);
        return (null, null);
    }
}

// Exposes the top-level-statement Program to WebApplicationFactory<Program> in Context.IntegrationTests.
public partial class Program;

// Captured once at startup so /api/system/status can report process uptime (CONTRACTS.md §12).
public readonly record struct HostStartTime(DateTimeOffset Value);
