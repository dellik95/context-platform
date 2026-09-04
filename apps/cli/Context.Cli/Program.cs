// Context.Cli — thin System.CommandLine adapter over Context.Application/Context.Infrastructure/
// Context.Claude. Every command below builds a small, self-contained composition root (the same
// AddContextX methods Context.Dashboard.Server is expected to call — see CONTRACTS.md §11 and each project's
// own ServiceCollectionExtensions), pointed at the resolved project root, and talks to the rest of
// the platform exclusively through IContextEngine / IIndexingPipeline / IClaudeIntegrationInstaller
// and the Context.Infrastructure repositories — never anything lower-level than that facade.
//
// Exit codes (per task brief): 0 success, 1 user error (bad args / not found), 2 unexpected failure.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Context.Application;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Claude;
using Context.Claude.Installation;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph;
using Context.Graph.Abstractions;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// ---------------------------------------------------------------------------------------------
// Global options
// ---------------------------------------------------------------------------------------------

var rootOption = new Option<string>(
    "--root",
    () => Directory.GetCurrentDirectory(),
    "Project root directory (defaults to the current directory). The Context Platform database is " +
    "shared across projects — see CONTEXT_PLATFORM_DATA_DIR / SharedDataLocation for where it lives.");

var rootCommand = new RootCommand(
    "Context Platform CLI — persistent project memory, code graph and semantic search for Claude Code.");
rootCommand.AddGlobalOption(rootOption);

// ---------------------------------------------------------------------------------------------
// context project ...
// ---------------------------------------------------------------------------------------------

var projectCommand = new Command("project", "Manage registered projects.");

var createNameOption = new Option<string>("--name", "Display name for the project.") { IsRequired = true };
var createPathOption = new Option<string?>(
    "--path", "Filesystem path the project's source tree lives at (defaults to --root).");
var createCommand = new Command("create", "Register a new project.");
createCommand.AddOption(createNameOption);
createCommand.AddOption(createPathOption);
createCommand.SetHandler(HandleProjectCreateAsync);

var listCommand = new Command("list", "List every project registered in this root's database.");
listCommand.SetHandler(HandleProjectListAsync);

var statusProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var statusCommand = new Command("status", "Show aggregate stats for a project.");
statusCommand.AddOption(statusProjectOption);
statusCommand.SetHandler(HandleProjectStatusAsync);

var initProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var initCommand = new Command("init", "Install/repair the Claude Code integration (hooks, .mcp.json) for a project.");
initCommand.AddOption(initProjectOption);
Func<InvocationContext, Task> initHandler = context => HandleClaudeInstallAsync(context, initProjectOption);
initCommand.SetHandler(initHandler);

projectCommand.AddCommand(createCommand);
projectCommand.AddCommand(listCommand);
projectCommand.AddCommand(statusCommand);
projectCommand.AddCommand(initCommand);
rootCommand.AddCommand(projectCommand);

// ---------------------------------------------------------------------------------------------
// context index
// ---------------------------------------------------------------------------------------------

var indexFullOption = new Option<bool>("--full", () => false, "Reprocess every file regardless of content-hash cache.");
var indexProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var indexCommand = new Command("index", "Scan and (re)index the project's source tree.");
indexCommand.AddOption(indexFullOption);
indexCommand.AddOption(indexProjectOption);
indexCommand.SetHandler(HandleIndexAsync);
rootCommand.AddCommand(indexCommand);

// ---------------------------------------------------------------------------------------------
// context search "<query>"
// ---------------------------------------------------------------------------------------------

var searchQueryArgument = new Argument<string>("query", "Free-text search query.");
var searchProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var searchBudgetOption = new Option<int>("--budget", () => 2000, "Token budget for the returned context pack.");
var searchCommand = new Command("search", "Search project code + memory and build a ranked context pack.");
searchCommand.AddArgument(searchQueryArgument);
searchCommand.AddOption(searchProjectOption);
searchCommand.AddOption(searchBudgetOption);
searchCommand.SetHandler(HandleSearchAsync);
rootCommand.AddCommand(searchCommand);

// ---------------------------------------------------------------------------------------------
// context memory search "<query>"
// ---------------------------------------------------------------------------------------------

var memoryCommand = new Command("memory", "Memory subsystem commands.");

var memorySearchQueryArgument = new Argument<string>("query", "Free-text query over memory title/body.");
var memorySearchProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var memorySearchCommand = new Command("search", "Search this project's memories (decisions, discoveries, bug fixes, ...).");
memorySearchCommand.AddArgument(memorySearchQueryArgument);
memorySearchCommand.AddOption(memorySearchProjectOption);
memorySearchCommand.SetHandler(HandleMemorySearchAsync);

memoryCommand.AddCommand(memorySearchCommand);
rootCommand.AddCommand(memoryCommand);

// ---------------------------------------------------------------------------------------------
// context graph "<symbol name or id>"
// ---------------------------------------------------------------------------------------------

var graphSymbolArgument = new Argument<string>("symbol", "Symbol name (substring match) or symbol/node id (GUID).");
var graphProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var graphHopsOption = new Option<int>("--hops", () => 3, "Maximum traversal hops for the blast-radius query.");
var graphCommand = new Command("graph", "Show callers/implementers/covering tests (blast radius) for a symbol.");
graphCommand.AddArgument(graphSymbolArgument);
graphCommand.AddOption(graphProjectOption);
graphCommand.AddOption(graphHopsOption);
graphCommand.SetHandler(HandleGraphAsync);
rootCommand.AddCommand(graphCommand);

// ---------------------------------------------------------------------------------------------
// context claude install
// ---------------------------------------------------------------------------------------------

var claudeCommand = new Command("claude", "Claude Code integration commands.");
var claudeInstallProjectOption = new Option<string?>("--project", "Project id (GUID). Defaults to the project registered at --root.");
var claudeInstallCommand = new Command("install", "Install/repair the Claude Code integration (hooks, .mcp.json) for a project.");
claudeInstallCommand.AddOption(claudeInstallProjectOption);
Func<InvocationContext, Task> claudeInstallHandler = context => HandleClaudeInstallAsync(context, claudeInstallProjectOption);
claudeInstallCommand.SetHandler(claudeInstallHandler);

claudeCommand.AddCommand(claudeInstallCommand);
rootCommand.AddCommand(claudeCommand);

// ---------------------------------------------------------------------------------------------
// context doctor
// ---------------------------------------------------------------------------------------------

var doctorProjectOption = new Option<string?>("--project", "Project id (GUID). Only used to echo which project this root resolves to.");
var doctorCommand = new Command("doctor", "Diagnose Context Platform health for a project root.");
doctorCommand.AddOption(doctorProjectOption);
doctorCommand.SetHandler(HandleDoctorAsync);
rootCommand.AddCommand(doctorCommand);

// ---------------------------------------------------------------------------------------------

return await rootCommand.InvokeAsync(args);

// =================================================================================================
// Command handlers
// =================================================================================================

async Task HandleProjectCreateAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var name = context.ParseResult.GetValueForOption(createNameOption)!;
        var path = context.ParseResult.GetValueForOption(createPathOption);
        var fullPath = Path.GetFullPath(path ?? root);

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

            var existing = await projects.GetByRootPathAsync(fullPath, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                Console.WriteLine($"A project is already registered at that path:");
                PrintProject(existing);
                return;
            }

            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = name,
                RootPath = fullPath,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var saved = await projects.AddAsync(project, ct).ConfigureAwait(false);
            Console.WriteLine("Created project:");
            PrintProject(saved);
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleProjectListAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var all = await projects.ListAsync(ct).ConfigureAwait(false);

            if (all.Count == 0)
            {
                Console.WriteLine("No projects registered. Run 'context project create --name <name>' first.");
                return;
            }

            Console.WriteLine($"{all.Count} project(s):");
            foreach (var project in all)
            {
                PrintProject(project);
            }
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleProjectStatusAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var projectIdText = context.ParseResult.GetValueForOption(statusProjectOption);

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);

            var engine = scope.ServiceProvider.GetRequiredService<IContextEngine>();
            var info = (ProjectInfo)await engine.ProjectInfoAsync(project.Id, ct).ConfigureAwait(false);

            Console.WriteLine($"Project   : {info.Name} ({info.ProjectId})");
            Console.WriteLine($"Root      : {info.RootPath}");
            Console.WriteLine($"Files     : {info.FilesIndexed}");
            Console.WriteLine($"Symbols   : {info.SymbolsIndexed}");
            Console.WriteLine($"Memories  : {info.MemoriesCount}");
            Console.WriteLine($"Last index: {(info.LastIndexedAt is { } t ? t.ToString("u") : "never")}");
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleClaudeInstallAsync(InvocationContext context, Option<string?> projectOption)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var projectIdText = context.ParseResult.GetValueForOption(projectOption);

        // Preferred path: resolve/create the project against a shared, already-running
        // Context.Dashboard.Server over its admin-gated HTTP API instead of this process's own direct-DB
        // find-or-create — keeps the project's identity and bearer token consistent for every
        // machine/CLI invocation pointed at the same shared host. Only attempted for the default
        // "resolve by root" flow (no explicit --project id, which already names a specific,
        // presumably locally-known project); returns null — falling back to today's unchanged
        // direct-DB behavior below — whenever no admin token is configured, the host doesn't
        // answer, or project creation itself fails, matching Context.Dashboard.Server's zero-configuration
        // startup goal (see ContextHostOptions' own remarks).
        var hostResolution = string.IsNullOrWhiteSpace(projectIdText)
            ? await TryResolveProjectViaHostAsync(root, ct).ConfigureAwait(false)
            : null;

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();

            Project project;
            string? mintedToken = null;
            if (hostResolution is { } resolved)
            {
                (project, mintedToken, var hostBaseUrl) = resolved;
                Console.WriteLine($"Resolved project '{project.Name}' via Context.Dashboard.Server at {hostBaseUrl}.");
            }
            else
            {
                var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
                project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);
            }

            var installer = scope.ServiceProvider.GetRequiredService<IClaudeIntegrationInstaller>();
            await installer.InstallAsync(project.Id, project.RootPath, ct, mintedToken).ConfigureAwait(false);

            Console.WriteLine($"Installed Claude Code integration for '{project.Name}' at {project.RootPath}.");
            Console.WriteLine("Wrote: .claude/hooks/*, .claude/settings.json, .mcp.json, .claude-plugin/plugin.json, .claude/context-platform.local.json");
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

/// <summary>
/// Tries the preferred, shared-host project-resolution path for <c>context claude install</c>:
/// admin-token-authenticated <c>POST /api/projects</c> against a Context.Dashboard.Server process that is
/// already up and reachable. <c>POST /api/projects</c> is itself a find-or-create (201 for a
/// brand-new project with its one-and-only raw token in the response body; 409, with no token —
/// the raw value is never recoverable once minted — for one that already exists at this root), so
/// no separate "does it already exist" call is needed here.
/// <para/>
/// Returns <c>null</c> — meaning "fall back to the existing direct local-DB path" — whenever:
/// <c>CONTEXT_PLATFORM_ADMIN_TOKEN</c> isn't set; a quick <c>GET /health</c> against the host
/// doesn't get a response at all (connection refused/timeout — nothing here requires <c>/health</c>
/// to report itself <i>healthy</i>, only reachable); or the create call itself fails or is
/// rejected. This is what keeps a first-ever, fully offline <c>context claude install</c> working
/// with zero configuration, matching <c>ContextHostOptions</c>' own stated design goal.
/// </summary>
static async Task<(Project Project, string? Token, string HostBaseUrl)?> TryResolveProjectViaHostAsync(string root, CancellationToken ct)
{
    const string AdminTokenEnvVar = "CONTEXT_PLATFORM_ADMIN_TOKEN";
    var adminToken = Environment.GetEnvironmentVariable(AdminTokenEnvVar);
    if (string.IsNullOrWhiteSpace(adminToken))
    {
        return null;
    }

    // Same CONTEXT_PLATFORM_PORT env var / 47923 default ContextHostOptions.FromEnvironment and
    // ClaudeIntegrationOptions.HostPort already agree on — see the latter's own remarks. Context.Cli
    // deliberately doesn't reference Context.Dashboard.Server (that would drag its ASP.NET Core dependencies
    // into a CLI that must also run with no host present at all), so the port is re-resolved here
    // from the same env var by name rather than shared code.
    var port = Environment.GetEnvironmentVariable("CONTEXT_PLATFORM_PORT") is { Length: > 0 } portText
        && int.TryParse(portText, out var parsedPort)
        ? parsedPort
        : new ClaudeIntegrationOptions().HostPort;
    var baseUrl = $"http://127.0.0.1:{port}";
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(3) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

    try
    {
        // Any response at all (2xx or Health's own 503-when-unhealthy) proves the process is up;
        // an exception here means nothing is listening, which is the only case this function needs
        // to distinguish from "reachable".
        using var healthResponse = await http.GetAsync("/health", ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return null;
    }

    var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    if (string.IsNullOrWhiteSpace(name))
    {
        name = root;
    }

    try
    {
        using var createResponse = await http
            .PostAsJsonAsync("/api/projects", new HostCreateProjectRequest(name, root), jsonOptions, ct)
            .ConfigureAwait(false);

        if (createResponse.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict))
        {
            Console.Error.WriteLine(
                $"Warning: Context.Dashboard.Server at {baseUrl} rejected project registration " +
                $"({(int)createResponse.StatusCode} {createResponse.StatusCode}); falling back to direct local database access.");
            return null;
        }

        var dto = await createResponse.Content.ReadFromJsonAsync<HostProjectDto>(jsonOptions, ct).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        var project = new Project
        {
            Id = dto.Id,
            Name = dto.Name,
            RootPath = dto.RootPath,
            VcsRemote = dto.VcsRemote,
            CreatedAt = dto.CreatedAt
        };

        // Non-null only when this POST itself just minted the project (201) — see this method's
        // own remarks; null on 409 (already existed) is exactly what lets ClaudeIntegrationInstaller
        // fall through to reusing whatever token this machine already has on disk for it.
        return (project, dto.Token, baseUrl);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        Console.Error.WriteLine(
            $"Warning: could not register the project with Context.Dashboard.Server at {baseUrl} ({ex.Message}); " +
            "falling back to direct local database access.");
        return null;
    }
}

async Task HandleIndexAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var full = context.ParseResult.GetValueForOption(indexFullOption);
        var projectIdText = context.ParseResult.GetValueForOption(indexProjectOption);

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            Project project;
            using (var resolveScope = provider.CreateScope())
            {
                var projects = resolveScope.ServiceProvider.GetRequiredService<IProjectRepository>();
                project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);
            }

            var dispatcher = provider.GetRequiredService<JobDispatcher>();
            await dispatcher.StartAsync(ct).ConfigureAwait(false);
            try
            {
                Console.WriteLine($"Indexing '{project.Name}' at {project.RootPath}{(full ? " (full rebuild)" : string.Empty)}...");

                using (var indexScope = provider.CreateScope())
                {
                    var pipeline = indexScope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
                    await pipeline.ReindexProjectAsync(project.Id, full, ct).ConfigureAwait(false);
                }

                await WaitForJobsToDrainAsync(provider, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
            }
            finally
            {
                await dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            using var infoScope = provider.CreateScope();
            var engine = infoScope.ServiceProvider.GetRequiredService<IContextEngine>();
            var info = (ProjectInfo)await engine.ProjectInfoAsync(project.Id, ct).ConfigureAwait(false);
            Console.WriteLine($"Done. Files: {info.FilesIndexed}, Symbols: {info.SymbolsIndexed}.");
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleSearchAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var query = context.ParseResult.GetValueForArgument(searchQueryArgument);
        var projectIdText = context.ParseResult.GetValueForOption(searchProjectOption);
        var budget = context.ParseResult.GetValueForOption(searchBudgetOption);

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);

            var engine = scope.ServiceProvider.GetRequiredService<IContextEngine>();
            var searchQuery = new SearchQuery(project.Id, query, null, budget);
            var pack = await engine.SearchContextAsync(searchQuery, ct).ConfigureAwait(false);

            PrintContextPack(pack);
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleMemorySearchAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var query = context.ParseResult.GetValueForArgument(memorySearchQueryArgument);
        var projectIdText = context.ParseResult.GetValueForOption(memorySearchProjectOption);

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);

            var engine = scope.ServiceProvider.GetRequiredService<IContextEngine>();
            var memories = await engine.SearchMemoryAsync(project.Id, query, includeSuperseded: false, ct).ConfigureAwait(false);

            if (memories.Count == 0)
            {
                Console.WriteLine("No matching memories.");
                return;
            }

            Console.WriteLine($"{memories.Count} matching memor{(memories.Count == 1 ? "y" : "ies")}:");
            foreach (var memory in memories)
            {
                var flags = memory.Pinned ? ", pinned" : string.Empty;
                Console.WriteLine();
                Console.WriteLine($"[{memory.Type}] {memory.Title}  (confidence {memory.Confidence:0.00}, {memory.Tier}{flags})");
                Console.WriteLine($"  {Truncate(memory.Body, 300)}");
            }
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleGraphAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var symbolText = context.ParseResult.GetValueForArgument(graphSymbolArgument);
        var projectIdText = context.ParseResult.GetValueForOption(graphProjectOption);
        var hops = context.ParseResult.GetValueForOption(graphHopsOption);

        await RunWithProviderAsync(root, migrate: true, ct, async provider =>
        {
            using var scope = provider.CreateScope();
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);

            var symbolsRepo = scope.ServiceProvider.GetRequiredService<ISymbolRepository>();
            var (symbolId, label) = await ResolveGraphTargetAsync(symbolsRepo, project.Id, symbolText, ct).ConfigureAwait(false);

            var engine = scope.ServiceProvider.GetRequiredService<IContextEngine>();
            var impact = await engine.GraphImpactAsync(symbolId, hops, ct).ConfigureAwait(false);

            Console.WriteLine($"Blast radius for '{label}' ({symbolId}), up to {hops} hop(s):");
            PrintImpactSection("Callers", impact.Callers);
            PrintImpactSection("Implementers / subtypes", impact.Implementers);
            PrintImpactSection("Covering tests", impact.CoveringTests);
        }).ConfigureAwait(false);
    }).ConfigureAwait(false);
}

async Task HandleDoctorAsync(InvocationContext context)
{
    var ct = context.GetCancellationToken();
    context.ExitCode = await RunAsync(async () =>
    {
        var root = ResolveRoot(context.ParseResult.GetValueForOption(rootOption));
        var projectIdText = context.ParseResult.GetValueForOption(doctorProjectOption);

        Console.WriteLine($"Context Platform doctor — root: {root}");
        Console.WriteLine();

        var allOk = true;
        void Report(string check, bool ok, string detail)
        {
            Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {check}: {detail}");
            if (!ok)
            {
                allOk = false;
            }
        }

        // Doctor deliberately does NOT run migrations itself (unlike every other command) so this
        // check reflects the database's real, on-disk state rather than one this run just fixed.
        var provider = await InitializeAsync(root, migrate: false, ct).ConfigureAwait(false);
        try
        {
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ContextDbContext>();

                bool reachable;
                try
                {
                    reachable = await db.Database.CanConnectAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    reachable = false;
                    Report("Database reachable", false, ex.Message);
                }

                if (reachable)
                {
                    Report("Database reachable", true, db.Database.GetDbConnection().DataSource);

                    try
                    {
                        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
                        Report("Migrations up to date", pending.Count == 0,
                            pending.Count == 0 ? "no pending migrations" : $"{pending.Count} pending: {string.Join(", ", pending)}");
                    }
                    catch (Exception ex)
                    {
                        Report("Migrations up to date", false, ex.Message);
                    }
                }

                try
                {
                    var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
                    var project = await ResolveProjectAsync(projects, root, projectIdText, ct).ConfigureAwait(false);
                    Console.WriteLine($"      (resolved project: {project.Name} / {project.Id})");
                }
                catch (CliUserErrorException)
                {
                    Console.WriteLine("      (no project resolved for this root yet — run 'context project create')");
                }
            }

            var mcpConfigPath = Path.Combine(root, ".mcp.json");
            if (!System.IO.File.Exists(mcpConfigPath))
            {
                Report("MCP config present", false, $"{mcpConfigPath} does not exist — run 'context claude install'");
            }
            else
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(mcpConfigPath);
                    await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                    Report("MCP config present", true, mcpConfigPath);
                }
                catch (JsonException ex)
                {
                    Report("MCP config present", false, $"{mcpConfigPath} is not valid JSON: {ex.Message}");
                }
            }

            var bearerTokenPath = Path.Combine(root, ".claude", "context-platform.local.json");
            Report("Bearer token file present", System.IO.File.Exists(bearerTokenPath), bearerTokenPath);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine(allOk ? "All checks passed." : "One or more checks failed.");
        if (!allOk)
        {
            throw new CliUserErrorException("doctor found one or more issues (see above)");
        }
    }).ConfigureAwait(false);
}

// =================================================================================================
// Composition root / shared helpers
// =================================================================================================

static string ResolveRoot(string? root) => Path.GetFullPath(root ?? Directory.GetCurrentDirectory());

/// <summary>
/// Builds the same DI graph Context.Dashboard.Server is expected to build (CONTRACTS.md §11): every layer's own
/// <c>AddContextX</c> first, then <c>AddContextApplication</c> last so its seam adapters can see
/// everything they wire together. All of Context.Indexing/Graph/Retrieval/Memory/Providers are
/// reachable here purely through Context.Cli's existing project references to Context.Application
/// (which itself references them) — see CONTRACTS.md §11 on transitive project references; no new
/// ProjectReference is added.
/// </summary>
static async Task<ServiceProvider> InitializeAsync(string root, bool migrate, CancellationToken ct)
{
    // Same resolver Context.Dashboard.Server's ContextHostOptions.FromEnvironment uses, so a bare-metal CLI run
    // and the host process always agree on exactly one database file — see SharedDataLocation's own
    // remarks. The CLI never builds an IConfiguration, so this reads CONTEXT_PLATFORM_DATA_DIR
    // straight from the environment (SharedDataLocation.ResolveDataDir's fallback for exactly that).
    var dataDir = SharedDataLocation.ResolveDataDir();
    Directory.CreateDirectory(dataDir);
    var dbPath = SharedDataLocation.ResolveDbPath();

    // One-time carry-forward: a repo previously run against an older build may still have a
    // database at the old <root>/.context/context.db location — copy it into the new shared data
    // dir (never move: the old file is left in place as a safety net) so it doesn't appear to
    // vanish. Idempotent (no-ops once dbPath exists), so safe to call on every command.
    var migratedFromLegacyPath = SharedDataLocation.EnsureMigratedFromLegacyLocation(dbPath, root);
    if (migratedFromLegacyPath is not null)
    {
        Console.WriteLine($"Copied existing database from legacy location {migratedFromLegacyPath} to {dbPath}.");
    }

    var services = new ServiceCollection();
    services.AddContextInfrastructure(dbPath);
    services.AddContextIndexing();
    services.AddContextGraph();
    services.AddContextProviders();
    services.AddContextRetrieval();
    services.AddContextMemory();
    services.AddContextClaude();
    services.AddContextApplication(root, dataDir);

    var provider = services.BuildServiceProvider();

    if (migrate)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ContextDbContext>();
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
    }

    return provider;
}

static async Task RunWithProviderAsync(string root, bool migrate, CancellationToken ct, Func<ServiceProvider, Task> body)
{
    var provider = await InitializeAsync(root, migrate, ct).ConfigureAwait(false);
    try
    {
        await body(provider).ConfigureAwait(false);
    }
    finally
    {
        await provider.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Resolves the project a command should operate on: an explicit <c>--project</c> GUID always
/// wins; otherwise falls back to the single project registered at <paramref name="root"/>, and
/// finally to "the only project in this database" if exactly one exists.
/// </summary>
static async Task<Project> ResolveProjectAsync(IProjectRepository projects, string root, string? projectIdText, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(projectIdText))
    {
        if (!Guid.TryParse(projectIdText, out var id))
        {
            throw new CliUserErrorException($"'{projectIdText}' is not a valid project id (expected a GUID).");
        }

        return await projects.GetByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new CliUserErrorException($"No project found with id {id}.");
    }

    var byRoot = await projects.GetByRootPathAsync(Path.GetFullPath(root), ct).ConfigureAwait(false);
    if (byRoot is not null)
    {
        return byRoot;
    }

    var all = await projects.ListAsync(ct).ConfigureAwait(false);
    return all.Count switch
    {
        1 => all[0],
        0 => throw new CliUserErrorException(
            $"No project is registered at '{root}'. Run 'context project create --name <name>' first."),
        _ => throw new CliUserErrorException(
            $"{all.Count} projects are registered and none matches root '{root}'; specify --project <id>.")
    };
}

/// <summary>
/// Resolves the "symbol name or id" argument of <c>context graph</c>. A GUID is used as a
/// GraphNode/Symbol id directly (UpdateGraphJobHandler sets <c>GraphNode.Id = Symbol.Id</c>, so
/// this is exactly what <see cref="IContextEngine.GraphImpactAsync"/> expects); otherwise it's
/// treated as a (sub)string match against symbol qualified names.
/// </summary>
static async Task<(Guid Id, string Label)> ResolveGraphTargetAsync(
    ISymbolRepository symbols, Guid projectId, string symbolText, CancellationToken ct)
{
    if (Guid.TryParse(symbolText, out var directId))
    {
        return (directId, symbolText);
    }

    var matches = await symbols.SearchByNameAsync(projectId, symbolText, limit: 10, ct).ConfigureAwait(false);
    if (matches.Count == 0)
    {
        throw new CliUserErrorException($"No symbol matching '{symbolText}' was found. Try 'context index' first, or pass a node id.");
    }

    var exact = matches.FirstOrDefault(s =>
        string.Equals(s.Name, symbolText, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s.QualifiedName, symbolText, StringComparison.OrdinalIgnoreCase));
    if (exact is not null)
    {
        return (exact.Id, exact.QualifiedName);
    }

    if (matches.Count == 1)
    {
        return (matches[0].Id, matches[0].QualifiedName);
    }

    var listing = string.Join(", ", matches.Select(s => s.QualifiedName));
    throw new CliUserErrorException(
        $"'{symbolText}' matches {matches.Count} symbols; use an exact name or a symbol id. Matches: {listing}");
}

static async Task WaitForJobsToDrainAsync(ServiceProvider provider, TimeSpan timeout, CancellationToken ct)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        using var scope = provider.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var pending = await jobs.ListByStatusAsync(JobStatus.Pending, null, ct).ConfigureAwait(false);
        var running = await jobs.ListByStatusAsync(JobStatus.Running, null, ct).ConfigureAwait(false);
        if (pending.Count == 0 && running.Count == 0)
        {
            return;
        }

        await Task.Delay(200, ct).ConfigureAwait(false);
    }

    Console.WriteLine(
        "Warning: background jobs (embedding / graph updates) did not finish within the timeout; " +
        "they will resume the next time 'context index' runs.");
}

static async Task<int> RunAsync(Func<Task> action)
{
    try
    {
        await action().ConfigureAwait(false);
        return 0;
    }
    catch (CliUserErrorException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unexpected error: {ex.Message}");
        return 2;
    }
}

static void PrintProject(Project project)
{
    Console.WriteLine($"  {project.Id}  {project.Name,-30} {project.RootPath}");
}

static void PrintContextPack(ContextPack pack)
{
    Console.WriteLine($"Architecture summary: {pack.ArchitectureSummary}");
    Console.WriteLine();
    Console.WriteLine($"Primary hits ({pack.PrimaryHits.Count}):");
    foreach (var hit in pack.PrimaryHits)
    {
        Console.WriteLine($"  [{hit.Score:0.000}] {hit.Kind} {hit.Label}  (node {hit.NodeId})");
        if (!string.IsNullOrWhiteSpace(hit.Snippet))
        {
            Console.WriteLine($"      {Truncate(hit.Snippet, 200)}");
        }
    }

    if (pack.HistoricalNotes.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Historical notes:");
        foreach (var note in pack.HistoricalNotes)
        {
            Console.WriteLine($"  - {Truncate(note, 200)}");
        }
    }

    if (pack.OpenIssues.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Open issues:");
        foreach (var issue in pack.OpenIssues)
        {
            Console.WriteLine($"  - {Truncate(issue, 200)}");
        }
    }

    Console.WriteLine();
    var truncatedNote = pack.Truncated ? $" (truncated, {pack.MoreAvailable} more available)" : string.Empty;
    Console.WriteLine($"Estimated tokens: {pack.EstimatedTokens}{truncatedNote}");
}

static void PrintImpactSection(string title, IReadOnlyList<ImpactNode> nodes)
{
    Console.WriteLine();
    Console.WriteLine($"{title} ({nodes.Count}):");
    foreach (var node in nodes)
    {
        Console.WriteLine($"  [{node.Hops} hop(s)] {node.Path}");
    }
}

static string Truncate(string? text, int maxChars) =>
    string.IsNullOrEmpty(text) ? string.Empty : text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");

/// <summary>Thrown for expected, user-actionable failures — maps to exit code 1 (see <see cref="RunAsync"/>).</summary>
internal sealed class CliUserErrorException(string message) : Exception(message);

/// <summary>
/// Wire-format mirror of Context.Dashboard.Server's own <c>CreateProjectRequest</c>/<c>ProjectDto</c>
/// (Api/Dtos.cs) — kept private and minimal here rather than referencing Context.Dashboard.Server itself,
/// which would pull the whole ASP.NET Core host into a CLI that must also run with no host
/// present at all (see <see cref="TryResolveProjectViaHostAsync"/>'s own remarks).
/// </summary>
internal sealed record HostCreateProjectRequest(string Name, string RootPath);

internal sealed record HostProjectDto(Guid Id, string Name, string RootPath, string? VcsRemote, DateTimeOffset CreatedAt, string? Token);
