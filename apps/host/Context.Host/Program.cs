using System.Text.Json;
using Context.Application;
using Context.Claude;
using Context.Claude.Installation;
using Context.Domain.Entities;
using Context.Graph;
using Context.Host;
using Context.Host.Api;
using Context.Host.BackgroundServices;
using Context.Host.Hooks;
using Context.Host.Hubs;
using Context.Host.Mcp;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Microsoft.EntityFrameworkCore;
using Serilog;

var hostOptions = ContextHostOptions.FromEnvironment(new ConfigurationBuilder().AddEnvironmentVariables().Build());
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

    // ContentRootPath must NOT default to the current working directory: that's
    // CONTEXT_PLATFORM_ROOT's mounted project (e.g. Docker's WORKDIR /workspace), which has
    // nothing to do with where this executable — and the wwwroot published beside it — actually
    // live. Pin it to the binary's own directory so UseStaticFiles/MapFallbackToFile below (and
    // the Directory.Exists("wwwroot") check) resolve correctly regardless of the launch cwd.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });
    // AddSingleton<TService>(TService) requires a reference type; HostStartTime is a value type, so
    // register it through the non-generic Type/instance overload instead.
    builder.Services.AddSingleton(typeof(HostStartTime), new HostStartTime(DateTimeOffset.UtcNow));

    // Single-process, local-only host (spec's "one host process" decision): bind Kestrel per
    // ContextHostOptions.BindHost — loopback-only bare-metal, 0.0.0.0 in a container (see that
    // property's remarks for how the container case still keeps the same effective boundary).
    builder.WebHost.UseUrls($"http://{hostOptions.BindHost}:{hostOptions.Port}");

    builder.Host.UseSerilog();

    // Both calls are no-ops unless the process was actually launched by that platform's service
    // manager (Windows SCM / systemd), so it's safe to call both on every OS — this is what lets
    // the exact same published binary run interactively, as a Windows Service, or as a systemd
    // unit. See deploy/ for the corresponding service definitions.
    builder.Host.UseWindowsService(o => o.ServiceName = "ContextPlatformHost");
    builder.Host.UseSystemd();

    builder.Services.AddSingleton(hostOptions);
    builder.Services.AddSingleton<HostProjectContext>();
    builder.Services.AddSingleton<ClaudeSessionMap>();

    // --- Every layer's own AddContextX, in dependency order (CONTRACTS.md §11). ---
    builder.Services.AddContextInfrastructure(hostOptions.DbPath);
    builder.Services.AddContextIndexing();
    builder.Services.AddContextGraph();
    builder.Services.AddContextRetrieval();
    builder.Services.AddContextMemory();
    var providersOptions = BuildProvidersOptions(builder.Configuration);
    builder.Services.AddContextProviders(providersOptions);
    // AddContextProviders takes the options as a plain instance (see its own signature) and never
    // registers IOptions<ContextProvidersOptions> itself - ProjectSettingsService needs the real
    // values (not the framework's silently-empty default IOptions<T>) to seed a project's
    // never-configured settings from what this host is actually running with.
    builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(providersOptions));
    builder.Services.AddContextClaude(o => o.HostPort = hostOptions.Port);

    // Context.Application composes every layer above plus the seam adapters between them, and
    // registers JobDispatcher as the app's own IHostedService (HydrateOnStartupAsync then
    // StartProcessingAsync run automatically as part of the host's own startup - see
    // Context.Application.ServiceCollectionExtensions.AddJobDispatcher).
    builder.Services.AddContextApplication(hostOptions.ProjectDataDir);

    builder.Services.AddHostedService<FileWatcherHostedService>();

    builder.Services.AddOpenApi();

    // Every JSON response (Memory.Type/Tier, GraphNode.Kind, etc.) should read as "Decision"/
    // "Class", not a bare ordinal — the Dashboard and any other API consumer shouldn't need to
    // hardcode this project's enum member order.
    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    // Dashboard live updates (spec §13) — Angular clients join a per-project group and receive
    // ProjectStatusChanged pushes from ProjectEndpoints' background reindex, instead of polling.
    builder.Services.AddSignalR()
        .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddSingleton<IndexingHubNotifier>();

    // Overrides AddContextApplication's NullIndexingProgressReporter default (last AddSingleton for
    // a given interface wins) - same override mechanism AddContextProviders already relies on for
    // its own placeholder-provider registrations.
    builder.Services.AddSingleton<Context.Application.Indexing.IIndexingProgressReporter, Context.Host.Hubs.SignalRIndexingProgressReporter>();

    // The API/hub is loopback-only at the network level (ContextHostOptions.BindHost/Docker port
    // publish) — CORS here only governs which *browser origins* may call it, and a locally-run
    // Angular dev server (ng serve, a different port on the same machine) is exactly that case.
    // Permissive by design: anything that can already reach this loopback-bound port is already
    // within the same local-first trust boundary the rest of the app assumes.
    const string DashboardCorsPolicy = "Dashboard";
    builder.Services.AddCors(o => o.AddPolicy(DashboardCorsPolicy, p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

    // MCP server hosted inside this same ASP.NET Core process (spec §9.14/§10): the installed
    // ModelContextProtocol.AspNetCore preview exposes a streamable-HTTP transport that maps onto
    // the existing app via WithHttpTransport()/MapMcp() rather than stdio - see the remarks below
    // MapMcp() call for the resulting mismatch with ClaudeIntegrationOptions' stdio-based
    // .mcp.json entry.
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    // Unlike Context.Cli (which migrates explicitly per command), this is the only entrypoint
    // that owns the database for as long as the host process runs, so it must bring the schema
    // up to date itself on every startup — MigrateAsync is a no-op once already current, so this
    // is safe to run unconditionally (including on every container restart).
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

static ContextProvidersOptions BuildProvidersOptions(IConfiguration configuration)
{
    var openAiKey = configuration["CONTEXT_OPENAI_API_KEY"];
    var anthropicKey = configuration["CONTEXT_ANTHROPIC_API_KEY"];

    // A local Ollama install (CONTEXT_EMBEDDING_PROVIDER=ollama and/or CONTEXT_LLM_PROVIDER=ollama)
    // is the fully-offline, no-API-key upgrade path over the noop defaults: real embeddings for
    // search, and a real model for session summarization/memory extraction (Context.Memory) that
    // currently degrade to templated heuristics under noop. Base URL/model are overridable in case
    // Ollama isn't on localhost:11434 or a different model was pulled.
    var ollamaBaseUrl = configuration["CONTEXT_OLLAMA_BASE_URL"];
    var ollamaEmbeddingModel = configuration["CONTEXT_OLLAMA_EMBEDDING_MODEL"];
    var ollamaChatModel = configuration["CONTEXT_OLLAMA_CHAT_MODEL"];

    return new ContextProvidersOptions
    {
        EmbeddingProvider = configuration["CONTEXT_EMBEDDING_PROVIDER"] is { Length: > 0 } embedding ? embedding : "noop",
        LlmProvider = configuration["CONTEXT_LLM_PROVIDER"] is { Length: > 0 } llm ? llm : "noop",
        OpenAi = new Context.Providers.Embeddings.OpenAiOptions { ApiKey = openAiKey },
        Anthropic = new Context.Providers.Llm.AnthropicOptions { ApiKey = anthropicKey },
        Ollama = new Context.Providers.Embeddings.OllamaOptions
        {
            BaseUrl = ollamaBaseUrl is { Length: > 0 } ? ollamaBaseUrl : "http://localhost:11434",
            Model = ollamaEmbeddingModel is { Length: > 0 } ? ollamaEmbeddingModel : "nomic-embed-text"
        },
        OllamaChat = new Context.Providers.Llm.OllamaChatOptions
        {
            BaseUrl = ollamaBaseUrl is { Length: > 0 } ? ollamaBaseUrl : "http://localhost:11434",
            Model = ollamaChatModel is { Length: > 0 } ? ollamaChatModel : "llama3.1"
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
