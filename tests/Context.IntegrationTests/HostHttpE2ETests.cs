using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Context.Claude.Hooks;
using Context.Domain.Entities;
using Context.Dashboard.Server;
using Context.Dashboard.Server.Api;
using Context.Dashboard.Server.Hooks;
using Context.Dashboard.Server.Security;
using Context.Infrastructure;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Context.IntegrationTests;

/// <summary>
/// Real HTTP-level end-to-end coverage for <c>Context.Dashboard.Server</c>: boots the actual ASP.NET Core app
/// (Kestrel swapped for <c>TestServer</c> by <see cref="WebApplicationFactory{TEntryPoint}"/>, but
/// everything else — DI composition across every layer, EF Core/SQLite, the <c>JobDispatcher</c>
/// hosted service, <c>ProjectFileWatcherManager</c>, and hook/REST/MCP bearer-token auth — wired
/// exactly as production does) and drives it purely through its public HTTP surface. This is
/// deliberately separate from <c>GoldenPathE2ETests</c> (same project), which exercises the engine
/// via direct DI composition with no HTTP involved at all.
/// </summary>
[Collection(CollectionName)]
public sealed class HostHttpE2ETests : IClassFixture<HostHttpE2ETests.HostHttpWebApplicationFactory>
{
    /// <summary>
    /// Shared with <see cref="HostHttpProductionCorsTests"/> so xunit never runs that class's
    /// Program instance concurrently with this one's — both boot the real <c>Context.Dashboard.Server</c>
    /// <see cref="Program"/> via <see cref="WebApplicationFactory{TEntryPoint}"/>, and only one may
    /// ever be live at a time (see <see cref="HostHttpWebApplicationFactory"/>'s remarks: Program.cs
    /// sets the global static Serilog.Log.Logger). xunit runs test classes within one collection
    /// strictly sequentially — one class's IClassFixture fully constructed, run, and disposed
    /// before the next class in the collection starts — which is exactly the guarantee needed here.
    /// </summary>
    public const string CollectionName = "Context.Dashboard.Server HTTP (sequential — one live Program at a time)";

    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HostHttpWebApplicationFactory _factory;

    public HostHttpE2ETests(HostHttpWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// One sequential walk through the whole HTTP surface, sharing the single
    /// <see cref="HostHttpWebApplicationFactory"/>-backed <see cref="Program"/> instance for the
    /// class (see that type's remarks for why only one may ever be live at a time).
    /// </summary>
    [Fact]
    public async Task HostHttpSurface_HealthProjectHooksAndSearch_WorkEndToEndOverRealHttp()
    {
        using var client = _factory.CreateClient();

        await AssertHealthEndpointAsync(client);
        await AssertCorsAllowListAsync(client);
        var project = await AssertProjectCreateAndRoundTripAsync(client);
        await AssertProtectedEndpointRequiresBearerTokenAsync(client);
        await AssertCrossProjectAccessIsRejectedAsync(client, project);
        await AssertHookAuthAsync(client);
        await AssertSearchSmokeAsync(client, project);
    }

    /// <summary>GET /health: 2xx, and every status field <see cref="HealthEndpoints"/> aggregates reflects a healthy, freshly-migrated process.</summary>
    private static async Task AssertHealthEndpointAsync(HttpClient client)
    {
        var response = await client.GetAsync("/health");
        response.IsSuccessStatusCode.Should().BeTrue("the freshly-migrated database and every DI-wired provider should report healthy");

        var health = await response.Content.ReadFromJsonAsync<HealthResponse>(ResponseJsonOptions);
        health.Should().NotBeNull();
        health!.Healthy.Should().BeTrue();
        health.DatabaseReachable.Should().BeTrue("HealthEndpoints runs a real 'SELECT 1' against the migrated SQLite file");
        health.JobDispatcherAlive.Should().BeTrue();
        health.EmbeddingProvider.Should().NotBeNullOrWhiteSpace();
        health.RerankerProvider.Should().NotBeNullOrWhiteSpace();
        health.LlmProvider.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Builds a request carrying the given bearer token — every REST route but /health now requires
    /// one (BACKLOG-02), and the shared <c>client</c> deliberately has no default Authorization
    /// header so each call site's auth is explicit and visible. <paramref name="bearerToken"/> is
    /// explicit rather than defaulted to a single factory token because Phase 3 has three distinct
    /// tokens in play here — the shared admin token, the pre-seeded "hook project"'s own token, and
    /// whatever project-specific token an endpoint under test just minted.
    /// </summary>
    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url, string bearerToken, object? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            request.Content = JsonContent.Create(jsonBody);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return request;
    }

    /// <summary>
    /// POST /api/projects (admin-token-gated — a brand-new project has no bearer token of its own
    /// yet) then GET /api/projects/{id} using the project's OWN freshly-minted token (proving
    /// <c>ProjectScopeAuth.RequireOwnProjectFilter</c>'s per-project
    /// path, not just its admin fallback): the created project round-trips through the real
    /// repository/DB.
    /// </summary>
    private async Task<ProjectDto> AssertProjectCreateAndRoundTripAsync(HttpClient client)
    {
        var rootPath = Path.Combine(_factory.TempRoot, "widget-service");
        var expectedRootPath = Path.GetFullPath(rootPath);
        var createRequest = new CreateProjectRequest("Widget Service", rootPath);

        using var createHttpRequest = CreateAuthorizedRequest(HttpMethod.Post, "/api/projects", _factory.AdminToken, createRequest);
        var createResponse = await client.SendAsync(createHttpRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        createResponse.Headers.Location.Should().NotBeNull();

        var created = await createResponse.Content.ReadFromJsonAsync<ProjectDto>(ResponseJsonOptions);
        created.Should().NotBeNull();
        created!.Name.Should().Be("Widget Service");
        created.RootPath.Should().Be(expectedRootPath);
        created.Token.Should().NotBeNullOrWhiteSpace("CreateAsync mints and returns the project's own bearer token exactly once");
        createResponse.Headers.Location!.ToString().Should().Contain(created.Id.ToString());

        using var getHttpRequest = CreateAuthorizedRequest(HttpMethod.Get, $"/api/projects/{created.Id}", created.Token!);
        var getResponse = await client.SendAsync(getHttpRequest);
        getResponse.IsSuccessStatusCode.Should().BeTrue();

        var status = await getResponse.Content.ReadFromJsonAsync<ProjectStatusDto>(ResponseJsonOptions);
        status.Should().NotBeNull();
        status!.Project.Id.Should().Be(created.Id);
        status.Project.Name.Should().Be(created.Name);
        status.Project.RootPath.Should().Be(created.RootPath);
        status.FilesIndexed.Should().Be(0);
        status.SymbolsIndexed.Should().Be(0);
        status.MemoriesCount.Should().Be(0);

        return created;
    }

    /// <summary>POST /hooks/session-start: rejected (401) with no Authorization header, accepted with the correct bearer token from the local config file.</summary>
    private async Task AssertHookAuthAsync(HttpClient client)
    {
        var input = new SessionStartInput(SessionId: $"hook-auth-{Guid.NewGuid():N}", Source: "startup");

        var unauthorizedResponse = await client.PostAsJsonAsync("/hooks/session-start", input);
        unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no Authorization header was sent");

        using var authorizedRequest = new HttpRequestMessage(HttpMethod.Post, "/hooks/session-start")
        {
            Content = JsonContent.Create(input)
        };
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.BearerToken);

        var authorizedResponse = await client.SendAsync(authorizedRequest);
        authorizedResponse.IsSuccessStatusCode.Should().BeTrue("the bearer token matches what was written to context-platform.local.json before startup");

        var output = await authorizedResponse.Content.ReadFromJsonAsync<HookOutput>(ResponseJsonOptions);
        output.Should().NotBeNull();
        output!.Block.Should().BeFalse();
    }

    /// <summary>
    /// GET /api/search against the freshly-created project's own bearer token, with nothing
    /// indexed: a well-formed, empty pack — not an error — proving the whole search DI chain is
    /// wired. <see cref="SearchEndpoints"/> derives the project entirely from the resolved bearer
    /// token (<c>CurrentProjectAccessor</c>), not from any client-supplied project id, so the
    /// project's own <see cref="ProjectDto.Token"/> is what selects which project is searched.
    /// </summary>
    private async Task AssertSearchSmokeAsync(HttpClient client, ProjectDto project)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "/api/search?q=hello%20world", project.Token!);
        var response = await client.SendAsync(request);
        response.IsSuccessStatusCode.Should().BeTrue("search over an unindexed project must degrade to an empty pack, never an error");

        var pack = await response.Content.ReadFromJsonAsync<ContextPack>(ResponseJsonOptions);
        pack.Should().NotBeNull();
        pack!.PrimaryHits.Should().NotBeNull().And.BeEmpty();
        pack.HistoricalNotes.Should().NotBeNull().And.BeEmpty();
        pack.OpenIssues.Should().NotBeNull().And.BeEmpty();
        pack.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// GET /api/sessions: 401 with no Authorization header, 200 with the pre-seeded "hook
    /// project"'s own bearer token — spot-checks that <c>BearerTokenAuth.RequireBearerTokenFilter</c>
    /// is really wired onto a plain per-project REST group (<see cref="SessionEndpoints"/>), not
    /// just onto /hooks (BACKLOG-02). Deliberately NOT /api/projects here: project creation/listing
    /// are admin-token-gated now (see <see cref="AssertProjectCreateAndRoundTripAsync"/>), not
    /// reachable with a per-project bearer token at all.
    /// </summary>
    private async Task AssertProtectedEndpointRequiresBearerTokenAsync(HttpClient client)
    {
        var unauthorizedResponse = await client.GetAsync("/api/sessions");
        unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no Authorization header was sent");

        using var authorizedRequest = CreateAuthorizedRequest(HttpMethod.Get, "/api/sessions", _factory.BearerToken);
        var authorizedResponse = await client.SendAsync(authorizedRequest);
        authorizedResponse.IsSuccessStatusCode.Should().BeTrue("the correct bearer token was sent");
    }

    /// <summary>
    /// A valid bearer token authenticates one project, and only that project. This fixture has two
    /// real projects in the same database — the pre-seeded "Hook Project" and the "Widget Service"
    /// just created — so it can prove the cross-tenant case for both ways a route names a project:
    /// the <c>{id}</c> route value (settings, Claude install) and the <c>projectId</c> query
    /// parameter (memories, jobs, usage, hooks status, symbols). Each is asserted to be rejected
    /// with the *other* project's token, and accepted with its own.
    /// </summary>
    private async Task AssertCrossProjectAccessIsRejectedAsync(HttpClient client, ProjectDto project)
    {
        var otherId = _factory.ProjectId;
        var ownToken = project.Token!;

        string[] foreignRouteIdUrls =
        [
            $"/api/projects/{otherId}/settings",
            $"/api/projects/{otherId}",
        ];

        foreach (var url in foreignRouteIdUrls)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, ownToken);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "{0} names a project this bearer token does not authorize", url);
        }

        using (var installRequest = CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/projects/{otherId}/claude/install", ownToken, new { }))
        {
            var installResponse = await client.SendAsync(installRequest);
            installResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "installing into another project's root would rewrite that project's .mcp.json and hook scripts");
        }

        // The admin token acts on any project — except here, where the authenticating token is also
        // the value written into the project's own on-disk integration files.
        using (var adminInstallRequest = CreateAuthorizedRequest(
            HttpMethod.Post, $"/api/projects/{otherId}/claude/install", _factory.AdminToken, new { }))
        {
            var adminInstallResponse = await client.SendAsync(adminInstallRequest);
            adminInstallResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "the admin token is not a project token, and must never be written into a repo's .mcp.json");
        }

        string[] foreignQueryUrls =
        [
            $"/api/memories?projectId={otherId}&q=anything",
            $"/api/jobs?projectId={otherId}",
            $"/api/usage?projectId={otherId}",
            $"/api/hooks/status?projectId={otherId}",
            $"/api/symbols?projectId={otherId}&q=anything",
        ];

        foreach (var url in foreignQueryUrls)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, ownToken);
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "{0} carries another project's id in its query string", url);
        }

        // Same routes, own project: the scoping must not have locked callers out of their own data.
        string[] ownUrls =
        [
            $"/api/projects/{project.Id}/settings",
            $"/api/memories?projectId={project.Id}&q=anything",
            "/api/memories?q=anything",
            $"/api/jobs?projectId={project.Id}",
            "/api/jobs",
            $"/api/usage?projectId={project.Id}",
            $"/api/hooks/status?projectId={project.Id}",
        ];

        foreach (var url in ownUrls)
        {
            using var request = CreateAuthorizedRequest(HttpMethod.Get, url, ownToken);
            var response = await client.SendAsync(request);
            response.IsSuccessStatusCode.Should().BeTrue(
                "{0} names the caller's own project (or no project at all, which defaults to it)", url);
        }
    }

    /// <summary>
    /// With CORS restricted to an explicit dev allow-list (BACKLOG-01), a request from an allowed
    /// dev origin gets that exact origin echoed back in Access-Control-Allow-Origin; a request from
    /// an origin NOT on the list gets no such header at all — never a wildcard, never silently
    /// allowed. Uses /health (unauthenticated) so this isolates the CORS behavior from bearer auth.
    /// </summary>
    private static async Task AssertCorsAllowListAsync(HttpClient client)
    {
        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        allowedRequest.Headers.Add("Origin", "http://localhost:4200");
        var allowedResponse = await client.SendAsync(allowedRequest);
        allowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowedValues)
            .Should().BeTrue("http://localhost:4200 is on the dev CORS allow-list");
        allowedValues!.Should().ContainSingle().Which.Should().Be("http://localhost:4200");

        using var disallowedRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        disallowedRequest.Headers.Add("Origin", "https://evil.example.com");
        var disallowedResponse = await client.SendAsync(disallowedRequest);
        disallowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out _)
            .Should().BeFalse("an origin outside the explicit allow-list must never see this header, wildcard or otherwise");
    }

    /// <summary>
    /// A <see cref="WebApplicationFactory{TEntryPoint}"/> for the real <c>Context.Dashboard.Server</c>
    /// <see cref="Program"/>, isolated into its own throwaway project root.
    /// </summary>
    /// <remarks>
    /// <b>Why setup happens in this constructor, not in the test class:</b> <c>Program.cs</c> reads
    /// <c>CONTEXT_PLATFORM_ROOT</c> (via <see cref="ContextHostOptions.FromEnvironment"/>) at the
    /// very top of its top-level statements, which only run once the base class lazily builds the
    /// host — on the first <see cref="WebApplicationFactory{TEntryPoint}.CreateClient"/>/server
    /// access. Everything below (temp root, migrated database, installed bearer token, the env var
    /// itself) must therefore exist before that first access, and a fixture constructor — run once
    /// by xunit before any test in the class executes — is exactly that point.
    /// <para/>
    /// <b>Why the database is migrated by hand here:</b> <c>Program.cs</c> does migrate at startup,
    /// but this fixture has to seed its pre-Phase-3 <c>Project</c> row (see below) into a schema
    /// that exists <i>before</i> the host is ever built — so the schema has to be created here.
    /// <para/>
    /// <b>Why only one instance is ever live:</b> <c>Program.cs</c> sets the global static
    /// <c>Serilog.Log.Logger</c>; a second concurrently-running instance would race on it. This
    /// class is shared for the whole test class via <see cref="IClassFixture{TFixture}"/>, and the
    /// single <see cref="Fact"/> above makes every HTTP call sequentially against it.
    /// </remarks>
    public sealed class HostHttpWebApplicationFactory : WebApplicationFactory<Program>
    {
        public string TempRoot { get; }

        /// <summary>
        /// The pre-seeded "hook project"'s own per-project bearer token (see the constructor's
        /// remarks on <see cref="ProjectTokenBackfill"/>) — what a hook script or any other
        /// per-project caller presents to authenticate as THIS project specifically.
        /// </summary>
        public string BearerToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        /// <summary>
        /// The shared <c>CONTEXT_PLATFORM_ADMIN_TOKEN</c> (see <see cref="AdminTokenAuth"/>) — gates
        /// project creation/listing, which a not-yet-existing project's own <see cref="BearerToken"/>
        /// cannot authorize (Phase 3).
        /// </summary>
        public string AdminToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        public Guid ProjectId { get; } = Guid.NewGuid();

        public HostHttpWebApplicationFactory()
        {
            TempRoot = Path.Combine(Path.GetTempPath(), "context-platform-host-http-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempRoot);

            var dataDir = Path.Combine(TempRoot, ".context");
            Directory.CreateDirectory(dataDir);
            var dbPath = Path.Combine(dataDir, "context.db");

            var migrationOptions = new DbContextOptionsBuilder<ContextDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            using (var migrationContext = new ContextDbContext(migrationOptions))
            {
                migrationContext.Database.Migrate();
            }

            // Seeds a Project row with NO BearerTokenHash yet — exactly the pre-Phase-3-install
            // shape ProjectTokenBackfill.ReconcileAsync exists to reconcile. Program.cs runs that
            // reconciliation once at startup (right after migration, still ahead of any test HTTP
            // call), and it hashes BearerToken below into this row by matching RootPath against the
            // local config file written next — so by the time AssertHookAuthAsync runs, BearerToken
            // really does authenticate as this project, the same way a pre-Phase-3 install's token
            // keeps working after an upgrade rather than needing a re-install.
            using (var seedContext = new ContextDbContext(migrationOptions))
            {
                seedContext.Projects.Add(new Project
                {
                    Id = ProjectId,
                    Name = "Hook Project",
                    RootPath = Path.GetFullPath(TempRoot),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                seedContext.SaveChanges();
            }

            var claudeDir = Path.Combine(TempRoot, ".claude");
            Directory.CreateDirectory(claudeDir);
            var localConfig = new HookLocalConfig(
                ProjectId: ProjectId.ToString(),
                Port: ContextHostOptions.DefaultPort,
                BearerToken: BearerToken,
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"));
            System.IO.File.WriteAllText(Path.Combine(claudeDir, "context-platform.local.json"), JsonSerializer.Serialize(localConfig));

            // Must land before this factory's first lazy host build (the test method's first
            // CreateClient() call) — see the type-level remarks above. CONTEXT_PLATFORM_DATA_DIR is
            // pinned to this fixture's own isolated dataDir too: ContextHostOptions.FromEnvironment
            // now resolves the database via SharedDataLocation (a shared, machine-level default),
            // which would otherwise point every test fixture at the SAME on-disk database.
            // CONTEXT_PLATFORM_ADMIN_TOKEN is read once by AdminTokenProvider via IConfiguration,
            // whose environment-variable provider loads at host build time same as the other two.
            Environment.SetEnvironmentVariable("CONTEXT_PLATFORM_ROOT", TempRoot);
            Environment.SetEnvironmentVariable("CONTEXT_PLATFORM_DATA_DIR", dataDir);
            Environment.SetEnvironmentVariable("CONTEXT_PLATFORM_ADMIN_TOKEN", AdminToken);
        }

        /// <summary>
        /// Pinned explicitly rather than relying on WebApplicationFactory's ambient/default
        /// environment resolution, so <see cref="AssertCorsAllowListAsync"/>'s "dev CORS policy is
        /// active" assertion is deterministic regardless of how the test runner's own
        /// ASPNETCORE_ENVIRONMENT happens to be set. Contrast with
        /// <see cref="HostHttpProductionCorsTests.ProductionWebApplicationFactory"/>, pinned to
        /// Production instead.
        /// </summary>
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing)
            {
                return;
            }

            try
            {
                Directory.Delete(TempRoot, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup only: a lingering SQLite -wal/-shm handle or a background job
                // still flushing at shutdown must never fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
