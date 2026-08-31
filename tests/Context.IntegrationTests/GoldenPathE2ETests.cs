using Context.Application;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph;
using Context.Indexing;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory;
using Context.Providers;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.IntegrationTests;

/// <summary>
/// True end-to-end test of the golden path described in specs/claudy-mem-specs.md §23
/// ("Definition of Success") and CONTRACTS.md §10's IIndexingPipeline/IContextEngine surface:
/// index a real (tiny) source tree, search for a symbol in it, run a session that edits a file,
/// end the session, and confirm the resulting memory can be found again by a later search.
///
/// No mocks anywhere: this composes the exact same DI graph apps/cli/Context.Cli/Program.cs builds
/// (AddContextInfrastructure -> AddContextIndexing -> AddContextGraph -> AddContextProviders ->
/// AddContextRetrieval -> AddContextMemory -> AddContextApplication), pointed at a fresh temp
/// directory that plays the role of both the indexed project root and the SQLite database
/// location, with every Context.Providers seam left at its offline/no-op default so the test needs
/// zero API keys and zero network access.
/// </summary>
public sealed class GoldenPathE2ETests : IAsyncLifetime
{
    private readonly DirectoryInfo _projectRoot = Directory.CreateTempSubdirectory("claudy-e2e-");
    private ServiceProvider? _provider;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        try
        {
            _projectRoot.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup only; a lingering SQLite file handle on Windows must never fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task GoldenPath_IndexSearchSessionAndMemoryRecall_WorksEndToEnd()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = cts.Token;

        // ---------------------------------------------------------------------------------------
        // Arrange: two tiny, real C# source files for the indexer to actually parse.
        // ---------------------------------------------------------------------------------------
        const string orderCalculatorRelativePath = "OrderCalculator.cs";
        await System.IO.File.WriteAllTextAsync(Path.Combine(_projectRoot.FullName, orderCalculatorRelativePath), """
            namespace Sample;

            /// <summary>Computes order totals for the golden-path fixture.</summary>
            public class OrderCalculator
            {
                public decimal ComputeTotal(int quantity, decimal unitPrice)
                {
                    return quantity * unitPrice;
                }
            }
            """, ct);

        await System.IO.File.WriteAllTextAsync(Path.Combine(_projectRoot.FullName, "InventoryService.cs"), """
            namespace Sample;

            public class InventoryService
            {
                public bool HasStock(string sku, int requestedQuantity)
                {
                    return requestedQuantity > 0;
                }
            }
            """, ct);

        var dataDir = Path.Combine(_projectRoot.FullName, ".context");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "context.db");

        // ---------------------------------------------------------------------------------------
        // Compose the real DI container, matching Context.Cli/Program.cs's InitializeAsync exactly
        // (AddContextProviders with no options => every provider defaults to noop/local-onnx, so
        // no API key and no network access is ever required).
        // ---------------------------------------------------------------------------------------
        var services = new ServiceCollection();
        services.AddContextInfrastructure(dbPath);
        services.AddContextIndexing();
        services.AddContextGraph();
        services.AddContextProviders();
        services.AddContextRetrieval();
        services.AddContextMemory();
        services.AddContextApplication(dataDir);

        _provider = services.BuildServiceProvider();

        await using (var migrationScope = _provider.CreateAsyncScope())
        {
            var db = migrationScope.ServiceProvider.GetRequiredService<ContextDbContext>();
            await db.Database.MigrateAsync(ct);
        }

        // ---------------------------------------------------------------------------------------
        // Step 2: register the Project row for the temp directory.
        // ---------------------------------------------------------------------------------------
        Project project;
        await using (var projectScope = _provider.CreateAsyncScope())
        {
            var projects = projectScope.ServiceProvider.GetRequiredService<IProjectRepository>();
            project = await projects.AddAsync(new Project
            {
                Id = Guid.NewGuid(),
                Name = "golden-path-fixture",
                RootPath = _projectRoot.FullName,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);
        }

        // ---------------------------------------------------------------------------------------
        // Steps 3-4: start the job dispatcher, run a full reindex, then drain EmbedChunk/UpdateGraph.
        // ---------------------------------------------------------------------------------------
        var dispatcher = _provider.GetRequiredService<JobDispatcher>();
        await dispatcher.StartAsync(ct);
        try
        {
            await using (var indexScope = _provider.CreateAsyncScope())
            {
                var pipeline = indexScope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
                await pipeline.ReindexProjectAsync(project.Id, full: true, ct);
            }

            await WaitForJobsToDrainAsync(_provider, TimeSpan.FromSeconds(60), ct);

            // -----------------------------------------------------------------------------------
            // Step 5: search for the fixture's symbol and confirm it comes back as a primary hit.
            // -----------------------------------------------------------------------------------
            ContextPack pack;
            await using (var searchScope = _provider.CreateAsyncScope())
            {
                var engine = searchScope.ServiceProvider.GetRequiredService<IContextEngine>();
                pack = await engine.SearchContextAsync(new SearchQuery(project.Id, "ComputeTotal", null, 2000), ct);
            }

            pack.PrimaryHits.Should().NotBeEmpty(
                "the golden-path fixture defines a ComputeTotal method that a full reindex should have made searchable");
            pack.PrimaryHits.Should().Contain(
                hit => hit.Label.Contains("ComputeTotal", StringComparison.Ordinal)
                    || (hit.Snippet != null && hit.Snippet.Contains("ComputeTotal", StringComparison.Ordinal)),
                "at least one hit should reference the ComputeTotal symbol or the source line that declares it");

            // -----------------------------------------------------------------------------------
            // Step 6: run a session that edits the fixture file, then end it.
            // -----------------------------------------------------------------------------------
            Guid sessionId;
            await using (var sessionScope = _provider.CreateAsyncScope())
            {
                var engine = sessionScope.ServiceProvider.GetRequiredService<IContextEngine>();
                sessionId = await engine.StartSessionAsync(project.Id, claudeSessionId: null, ct);

                await engine.RecordEventAsync(
                    sessionId, SessionEventType.FileEdit, new { path = orderCalculatorRelativePath }, ct);
                await engine.RecordEventAsync(
                    sessionId, SessionEventType.Prompt, new { text = "Add a ComputeTotal method to OrderCalculator." }, ct);

                await engine.EndSessionAsync(sessionId, ct);
            }

            // -----------------------------------------------------------------------------------
            // Step 7: drain SummarizeSession -> ExtractMemory.
            // -----------------------------------------------------------------------------------
            await WaitForJobsToDrainAsync(_provider, TimeSpan.FromSeconds(60), ct);

            // -----------------------------------------------------------------------------------
            // Step 8: a SessionSummary now exists, and at least one Change memory was extracted
            // from the offline heuristic candidate generator's ChangedFiles path (see
            // MemoryCandidateHeuristics.TryAddChangedFiles, fed by HeuristicSessionSummaryBuilder's
            // FileEdit-derived ChangedFilesJson).
            // -----------------------------------------------------------------------------------
            SessionSummary? summary;
            IReadOnlyList<DomainMemory> memories;
            await using (var assertScope = _provider.CreateAsyncScope())
            {
                var sessions = assertScope.ServiceProvider.GetRequiredService<ISessionRepository>();
                summary = await sessions.GetSummaryAsync(sessionId, ct);

                var memoryRepository = assertScope.ServiceProvider.GetRequiredService<IMemoryRepository>();
                memories = await memoryRepository.ListByProjectAsync(project.Id, includeSuperseded: false, ct);
            }

            summary.Should().NotBeNull("EndSessionAsync should have enqueued SummarizeSession, which persists a SessionSummary row");

            var changeMemory = memories.FirstOrDefault(m => m.Type == MemoryType.Change);
            changeMemory.Should().NotBeNull(
                "the offline MemoryCandidateHeuristics.TryAddChangedFiles path should turn the session's FileEdit event into a Change memory");
            changeMemory!.Body.Should().Contain(orderCalculatorRelativePath,
                "the Change memory's body lists the files touched this session");

            // -----------------------------------------------------------------------------------
            // Step 9: the next session picks up the prior memory via SearchMemoryAsync.
            // -----------------------------------------------------------------------------------
            IReadOnlyList<DomainMemory> recalled;
            await using (var recallScope = _provider.CreateAsyncScope())
            {
                var engine = recallScope.ServiceProvider.GetRequiredService<IContextEngine>();
                recalled = await engine.SearchMemoryAsync(project.Id, orderCalculatorRelativePath, includeSuperseded: false, ct);
            }

            recalled.Should().NotBeEmpty("searching for the edited file's path should recall the Change memory recorded about it");
            recalled.Should().Contain(m => m.Id == changeMemory.Id);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Mirrors Context.Cli/Program.cs's WaitForJobsToDrainAsync: polls IJobRepository until no
    /// Pending/Running jobs remain, bounded by <paramref name="timeout"/> so a genuine regression
    /// (a job stuck retrying, or a handler that never completes) fails the test instead of hanging.
    /// </summary>
    private static async Task WaitForJobsToDrainAsync(ServiceProvider provider, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = provider.CreateAsyncScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var pending = await jobs.ListByStatusAsync(JobStatus.Pending, ct);
            var running = await jobs.ListByStatusAsync(JobStatus.Running, ct);
            if (pending.Count == 0 && running.Count == 0)
            {
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new TimeoutException(
            $"Background jobs (embedding / graph / summarize / extract-memory) did not drain within {timeout}.");
    }
}
