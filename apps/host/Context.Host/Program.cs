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
using Context.Host.Mcp;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
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
        "Starting Context.Host — project root {ProjectRoot}, port {Port}", hostOptions.ProjectRoot, hostOptions.Port);

    var builder = WebApplication.CreateBuilder(args);

    // Single-process, local-only host (spec's "one host process" decision): bind Kestrel to
    // loopback only, on the configured/default port.
    builder.WebHost.UseUrls($"http://127.0.0.1:{hostOptions.Port}");

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
    builder.Services.AddContextProviders(BuildProvidersOptions(builder.Configuration));
    builder.Services.AddContextClaude(o => o.HostPort = hostOptions.Port);

    // Context.Application composes every layer above plus the seam adapters between them, and
    // registers JobDispatcher as the app's own IHostedService (HydrateOnStartupAsync then
    // StartProcessingAsync run automatically as part of the host's own startup - see
    // Context.Application.ServiceCollectionExtensions.AddJobDispatcher).
    builder.Services.AddContextApplication(hostOptions.ProjectDataDir);

    builder.Services.AddHostedService<FileWatcherHostedService>();

    builder.Services.AddOpenApi();

    // MCP server hosted inside this same ASP.NET Core process (spec §9.14/§10): the installed
    // ModelContextProtocol.AspNetCore preview exposes a streamable-HTTP transport that maps onto
    // the existing app via WithHttpTransport()/MapMcp() rather than stdio - see the remarks below
    // MapMcp() call for the resulting mismatch with ClaudeIntegrationOptions' stdio-based
    // .mcp.json entry.
    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly();

    var app = builder.Build();

    await InitializeHostProjectContextAsync(app, hostOptions.ProjectRoot).ConfigureAwait(false);

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.MapHookEndpoints();
    app.MapProjectEndpoints();
    app.MapSearchEndpoints();
    app.MapMemoryEndpoints();
    app.MapGraphEndpoints();
    app.MapHealthEndpoints();

    // NOTE: the generated .mcp.json entry (Context.Claude.Installation.ClaudeIntegrationInstaller /
    // ClaudeIntegrationOptions.McpCommand+McpArgs) assumes a stdio-launched MCP server
    // ("dotnet Context.Host.dll --stdio"), matching the spec's stated default transport. The
    // ModelContextProtocol.AspNetCore package actually installed (0.2.0-preview.3) instead offers
    // an HTTP/SSE transport that composes cleanly with this same ASP.NET Core app (WithHttpTransport
    // + MapMcp), which is what's wired here. These two don't match yet: as shipped, a project's
    // .mcp.json will try to spawn a second stdio process rather than talking to this endpoint.
    // Follow-up (tracked, not fixed here - Context.Claude is out of this task's scope): either give
    // Context.Host a "--stdio" launch mode that runs a stdio McpServer sharing the same DI
    // container, or change the installer to emit an HTTP-type .mcp.json entry
    // (`{"type":"http","url":"http://127.0.0.1:{port}/mcp"}`) pointing at the route mapped below.
    app.MapMcp("/mcp");

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

    return new ContextProvidersOptions
    {
        EmbeddingProvider = configuration["CONTEXT_EMBEDDING_PROVIDER"] is { Length: > 0 } embedding ? embedding : "noop",
        LlmProvider = configuration["CONTEXT_LLM_PROVIDER"] is { Length: > 0 } llm ? llm : "noop",
        OpenAi = new Context.Providers.Embeddings.OpenAiOptions { ApiKey = openAiKey },
        Anthropic = new Context.Providers.Llm.AnthropicOptions { ApiKey = anthropicKey }
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

    var project = await projects.GetByRootPathAsync(projectRoot, CancellationToken.None).ConfigureAwait(false);
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
