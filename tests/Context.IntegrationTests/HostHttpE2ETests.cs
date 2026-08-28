using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Context.Claude.Hooks;
using Context.Host;
using Context.Host.Api;
using Context.Host.Hooks;
using Context.Infrastructure;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Context.IntegrationTests;

/// <summary>
/// Real HTTP-level end-to-end coverage for <c>Context.Host</c>: boots the actual ASP.NET Core app
/// (Kestrel swapped for <c>TestServer</c> by <see cref="WebApplicationFactory{TEntryPoint}"/>, but
/// everything else — DI composition across every layer, EF Core/SQLite, the <c>JobDispatcher</c>
/// hosted service, <c>FileWatcherHostedService</c>, and hook bearer-token auth — wired exactly as
/// production does) and drives it purely through its public HTTP surface. This is deliberately
/// separate from <c>GoldenPathE2ETests</c> (same project), which exercises the engine via direct DI
/// composition with no HTTP involved at all.
/// </summary>
public sealed class HostHttpE2ETests : IClassFixture<HostHttpE2ETests.HostHttpWebApplicationFactory>
{
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
        var project = await AssertProjectCreateAndRoundTripAsync(client);
        await AssertHookAuthAsync(client);
        await AssertSearchSmokeAsync(client, project.Id);
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

    /// <summary>POST /api/projects then GET /api/projects/{id}: the created project round-trips through the real repository/DB.</summary>
    private async Task<ProjectDto> AssertProjectCreateAndRoundTripAsync(HttpClient client)
    {
        var rootPath = Path.Combine(_factory.TempRoot, "widget-service");
        var expectedRootPath = Path.GetFullPath(rootPath);
        var createRequest = new CreateProjectRequest("Widget Service", rootPath);

        var createResponse = await client.PostAsJsonAsync("/api/projects", createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        createResponse.Headers.Location.Should().NotBeNull();

        var created = await createResponse.Content.ReadFromJsonAsync<ProjectDto>(ResponseJsonOptions);
        created.Should().NotBeNull();
        created!.Name.Should().Be("Widget Service");
        created.RootPath.Should().Be(expectedRootPath);
        createResponse.Headers.Location!.ToString().Should().Contain(created.Id.ToString());

        var getResponse = await client.GetAsync($"/api/projects/{created.Id}");
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

    /// <summary>GET /api/search against a project with nothing indexed: a well-formed, empty pack — not an error — proving the whole search DI chain is wired.</summary>
    private static async Task AssertSearchSmokeAsync(HttpClient client, Guid projectId)
    {
        var response = await client.GetAsync($"/api/search?projectId={projectId}&q=hello%20world");
        response.IsSuccessStatusCode.Should().BeTrue("search over an unindexed project must degrade to an empty pack, never an error");

        var pack = await response.Content.ReadFromJsonAsync<ContextPack>(ResponseJsonOptions);
        pack.Should().NotBeNull();
        pack!.PrimaryHits.Should().NotBeNull().And.BeEmpty();
        pack.HistoricalNotes.Should().NotBeNull().And.BeEmpty();
        pack.OpenIssues.Should().NotBeNull().And.BeEmpty();
        pack.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// A <see cref="WebApplicationFactory{TEntryPoint}"/> for the real <c>Context.Host</c>
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
    /// <b>Why the database is migrated by hand here:</b> <c>Context.Host</c>'s own <c>Program.cs</c>
    /// never calls <c>Database.Migrate()</c> — in real usage that is the CLI's job (see
    /// <c>Context.Cli</c>'s <c>InitializeAsync(root, migrate: true, ...)</c>, run once by
    /// <c>context claude install</c>/<c>context init</c> before the host is ever started). Without
    /// this, <c>InitializeHostProjectContextAsync</c> would throw querying a schema-less database
    /// before a single route is even mapped.
    /// <para/>
    /// <b>Why only one instance is ever live:</b> <c>Program.cs</c> sets the global static
    /// <c>Serilog.Log.Logger</c>; a second concurrently-running instance would race on it. This
    /// class is shared for the whole test class via <see cref="IClassFixture{TFixture}"/>, and the
    /// single <see cref="Fact"/> above makes every HTTP call sequentially against it.
    /// </remarks>
    public sealed class HostHttpWebApplicationFactory : WebApplicationFactory<Program>
    {
        public string TempRoot { get; }

        public string BearerToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

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

            var claudeDir = Path.Combine(TempRoot, ".claude");
            Directory.CreateDirectory(claudeDir);
            var localConfig = new HookLocalConfig(
                ProjectId: Guid.NewGuid().ToString(),
                Port: ContextHostOptions.DefaultPort,
                BearerToken: BearerToken,
                GeneratedAt: DateTimeOffset.UtcNow.ToString("O"));
            File.WriteAllText(Path.Combine(claudeDir, "context-platform.local.json"), JsonSerializer.Serialize(localConfig));

            // Must land before this factory's first lazy host build (the test method's first
            // CreateClient() call) — see the type-level remarks above.
            Environment.SetEnvironmentVariable("CONTEXT_PLATFORM_ROOT", TempRoot);
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
