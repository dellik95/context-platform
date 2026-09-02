using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Context.Host;
using Context.Host.Hooks;
using Context.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Context.IntegrationTests;

/// <summary>
/// Same real-HTTP approach as <see cref="HostHttpE2ETests"/>, but against a
/// <see cref="Environments.Production"/> host, to prove BACKLOG-01's central guarantee: Production
/// registers no CORS policy at all, so no Access-Control-Allow-Origin header is ever emitted for
/// any Origin — not even one of the dev allow-list's own origins.
/// </summary>
/// <remarks>
/// Shares <see cref="HostHttpE2ETests.CollectionName"/> so xunit never runs this class's
/// WebApplicationFactory-backed <see cref="Program"/> concurrently with that class's. Both boot the
/// real Context.Host <c>Program</c>, which sets the global static Serilog.Log.Logger at startup —
/// only one instance may ever be live at a time. Test classes within the same xunit collection run
/// strictly sequentially (one class's IClassFixture fully built, exercised, and disposed before the
/// next class in the collection starts), which is exactly the guarantee this relies on.
/// </remarks>
[Collection(HostHttpE2ETests.CollectionName)]
public sealed class HostHttpProductionCorsTests : IClassFixture<HostHttpProductionCorsTests.ProductionWebApplicationFactory>
{
    private readonly ProductionWebApplicationFactory _factory;

    public HostHttpProductionCorsTests(ProductionWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ProductionHost_NeverEmitsCorsHeaders_ButStillServesHealthAndEnforcesBearerAuth()
    {
        using var client = _factory.CreateClient();

        // /health stays open in every environment, Origin header or not, and CORS never enters
        // the picture — Production never calls AddCors/UseCors, so the middleware isn't even in
        // the pipeline.
        using var devOriginRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        devOriginRequest.Headers.Add("Origin", "http://localhost:4200"); // one of the dev allow-list's own origins
        var devOriginResponse = await client.SendAsync(devOriginRequest);
        devOriginResponse.IsSuccessStatusCode.Should().BeTrue("/health must stay unauthenticated in every environment");
        devOriginResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out _).Should()
            .BeFalse("Production registers no CORS policy at all, so no header can be emitted for any origin, including dev ones");

        using var evilOriginRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        evilOriginRequest.Headers.Add("Origin", "https://evil.example.com");
        var evilOriginResponse = await client.SendAsync(evilOriginRequest);
        evilOriginResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out _).Should().BeFalse();

        // Bearer auth is still enforced in Production exactly as in Development (BACKLOG-02).
        var unauthorizedResponse = await client.GetAsync("/api/projects");
        unauthorizedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no Authorization header was sent");

        using var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _factory.BearerToken);
        var authorizedResponse = await client.SendAsync(authorizedRequest);
        authorizedResponse.IsSuccessStatusCode.Should().BeTrue("the correct bearer token was sent");
    }

    /// <summary>
    /// Mirrors <see cref="HostHttpE2ETests.HostHttpWebApplicationFactory"/> but pins
    /// <see cref="Environments.Production"/> instead of Development, and uses its own isolated
    /// temp root/database/token so it never shares state with that class's factory.
    /// </summary>
    public sealed class ProductionWebApplicationFactory : WebApplicationFactory<Program>
    {
        public string TempRoot { get; }

        public string BearerToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        public ProductionWebApplicationFactory()
        {
            TempRoot = Path.Combine(Path.GetTempPath(), "context-platform-host-http-prod-tests", Guid.NewGuid().ToString("N"));
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

            // Must land before this factory's first lazy host build (this class's Fact's first
            // CreateClient() call) — same reasoning as HostHttpE2ETests.HostHttpWebApplicationFactory.
            Environment.SetEnvironmentVariable("CONTEXT_PLATFORM_ROOT", TempRoot);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Production);
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
