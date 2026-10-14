using System.Text.Json;
using Context.Application.Adapters;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Repositories;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

/// <summary>
/// Runs the handler over the real reconsolidator and SQLite-backed store without a chat model, so the
/// plan is the offline one: two near-identical memories naming the same file merge by fact union.
/// </summary>
public sealed class ReconsolidateMemoryJobHandlerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-reconsolidate-tests-{Guid.NewGuid():N}.db");
    private readonly Guid _projectId = Guid.NewGuid();

    private DbContextOptions<ContextDbContext> Options =>
        new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options;

    [Fact]
    public async Task Dry_run_stores_a_pending_report_and_changes_no_memory()
    {
        await using var fixture = await CreateFixtureAsync();
        var (first, second) = await SeedDuplicatesAsync(fixture);

        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId)), CancellationToken.None);

        var stored = (await fixture.Memories.ListReportsAsync(_projectId, CancellationToken.None)).Should().ContainSingle().Subject;
        stored.Status.Should().Be(ConsolidationReportStatus.Pending);
        stored.AppliedAt.Should().BeNull();
        var report = ReconsolidationReport.FromJson(stored.ReportJson);
        report.Outcome.Should().BeNull();
        report.Groups.Should().ContainSingle().Which.Canonicals.Should().ContainSingle()
            .Which.SourceIds.Should().BeEquivalentTo(new[] { first.Id, second.Id });

        var memories = await LiveAsync(fixture);
        memories.Select(m => m.Id).Should().BeEquivalentTo(new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task Applying_a_reviewed_report_merges_the_group_and_marks_the_report_applied()
    {
        await using var fixture = await CreateFixtureAsync();
        var (first, second) = await SeedDuplicatesAsync(fixture);
        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId)), CancellationToken.None);
        var pending = await ClaimPendingReportAsync(fixture);

        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: pending.Id)), CancellationToken.None);

        var applied = await fixture.Memories.GetReportAsync(pending.Id, CancellationToken.None);
        applied!.Status.Should().Be(ConsolidationReportStatus.Applied);
        applied.AppliedAt.Should().NotBeNull();
        ReconsolidationReport.FromJson(applied.ReportJson).Outcome!.AppliedGroups.Should().Equal(1);
        AssertMerged(await LiveAsync(fixture), await fixture.Db.Memories.AsNoTracking().ToListAsync(), first, second);
    }

    [Fact]
    public async Task Scheduled_run_plans_and_applies_at_once()
    {
        await using var fixture = await CreateFixtureAsync();
        var (first, second) = await SeedDuplicatesAsync(fixture);

        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false)), CancellationToken.None);

        var report = (await fixture.Memories.ListReportsAsync(_projectId, CancellationToken.None)).Should().ContainSingle().Subject;
        report.Status.Should().Be(ConsolidationReportStatus.Applied);
        AssertMerged(await LiveAsync(fixture), await fixture.Db.Memories.AsNoTracking().ToListAsync(), first, second);
    }

    [Theory]
    [InlineData(ConsolidationReportStatus.Pending)]
    [InlineData(ConsolidationReportStatus.Discarded)]
    [InlineData(ConsolidationReportStatus.Applied)]
    public async Task Only_a_report_claimed_for_applying_is_applied(ConsolidationReportStatus status)
    {
        await using var fixture = await CreateFixtureAsync();
        var (first, second) = await SeedDuplicatesAsync(fixture);
        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId)), CancellationToken.None);
        var pending = (await fixture.Memories.ListReportsAsync(_projectId, CancellationToken.None)).Single();
        if (status != ConsolidationReportStatus.Pending)
        {
            (await fixture.Memories.SetReportStatusAsync(pending.Id, ConsolidationReportStatus.Pending, status, null, null, CancellationToken.None)).Should().BeTrue();
        }

        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: pending.Id)), CancellationToken.None);

        (await fixture.Memories.GetReportAsync(pending.Id, CancellationToken.None))!.Status.Should().Be(status);
        (await LiveAsync(fixture)).Select(m => m.Id).Should().BeEquivalentTo(new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task An_apply_that_throws_before_the_last_attempt_keeps_the_report_applying_so_the_retry_applies_it()
    {
        await using var fixture = await CreateFixtureAsync();
        var (first, second) = await SeedDuplicatesAsync(fixture);
        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId)), CancellationToken.None);
        var claimed = await ClaimPendingReportAsync(fixture);
        fixture.Embeddings.Fail = true;
        var payload = new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: claimed.Id);

        await fixture.Invoking(f => f.Sut.HandleAsync(MakeJob(payload, attempts: 1), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        (await fixture.Memories.GetReportAsync(claimed.Id, CancellationToken.None))!.Status.Should().Be(ConsolidationReportStatus.Applying);
        (await LiveAsync(fixture)).Select(m => m.Id).Should().BeEquivalentTo(new[] { first.Id, second.Id });

        fixture.Embeddings.Fail = false;
        await fixture.Sut.HandleAsync(MakeJob(payload, attempts: 2), CancellationToken.None);

        (await fixture.Memories.GetReportAsync(claimed.Id, CancellationToken.None))!.Status.Should().Be(ConsolidationReportStatus.Applied);
        (await LiveAsync(fixture)).Should().ContainSingle("the retry applied the report instead of finding nothing to do");
    }

    [Fact]
    public async Task An_apply_that_throws_on_the_last_attempt_marks_the_report_failed_and_a_requeue_applies_it_again()
    {
        await using var fixture = await CreateFixtureAsync();
        var (first, second) = await SeedDuplicatesAsync(fixture);
        await fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId)), CancellationToken.None);
        var claimed = await ClaimPendingReportAsync(fixture);
        fixture.Embeddings.Fail = true;
        var payload = new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: claimed.Id);
        var lastAttempt = JobRetryPolicy.For(JobType.ReconsolidateMemory).MaxAttempts;

        await fixture.Invoking(f => f.Sut.HandleAsync(MakeJob(payload, attempts: lastAttempt), CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        (await fixture.Memories.GetReportAsync(claimed.Id, CancellationToken.None))!.Status.Should().Be(ConsolidationReportStatus.Failed);
        (await LiveAsync(fixture)).Select(m => m.Id).Should().BeEquivalentTo(new[] { first.Id, second.Id });

        fixture.Embeddings.Fail = false;
        await fixture.Sut.HandleAsync(MakeJob(payload, attempts: 1), CancellationToken.None);

        (await fixture.Memories.GetReportAsync(claimed.Id, CancellationToken.None))!.Status.Should().Be(ConsolidationReportStatus.Applied);
        (await LiveAsync(fixture)).Should().ContainSingle("a manual retry of the failed job applies the Failed report");
    }

    [Fact]
    public async Task A_scheduled_run_that_throws_closes_its_own_report_because_the_retry_plans_a_new_one()
    {
        await using var fixture = await CreateFixtureAsync();
        await SeedDuplicatesAsync(fixture);
        fixture.Embeddings.Fail = true;

        await fixture.Invoking(f => f.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false), attempts: 1), CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        var report = (await fixture.Memories.ListReportsAsync(_projectId, CancellationToken.None)).Single();
        report.Status.Should().Be(ConsolidationReportStatus.Failed, "it would otherwise stay Applying with no job to resume it");
    }

    [Fact]
    public async Task A_report_of_another_project_fails_the_job()
    {
        await using var fixture = await CreateFixtureAsync();
        var otherProjectId = Guid.NewGuid();
        fixture.Db.Projects.Add(new Project { Id = otherProjectId, Name = "Billing", RootPath = "/repo/billing", CreatedAt = DateTimeOffset.UtcNow });
        await fixture.Db.SaveChangesAsync();
        var foreign = await fixture.Memories.AddReportAsync(
            new MemoryConsolidationReport
            {
                ProjectId = otherProjectId,
                CreatedAt = DateTimeOffset.UtcNow,
                ReportJson = new ReconsolidationReport([], [], 0, new ReconsolidationStats(0, 0, 0, 0, 0, 0, 0, 0, false)).ToJson(),
            },
            CancellationToken.None);

        var act = () => fixture.Sut.HandleAsync(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: foreign.Id)), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Startup_sweep_fails_only_applying_reports_no_queued_job_will_resume()
    {
        await using var fixture = await CreateFixtureAsync();
        var resumable = await AddReportAsync(fixture, ConsolidationReportStatus.Applying);
        var queued = await AddReportAsync(fixture, ConsolidationReportStatus.Applying);
        var orphaned = await AddReportAsync(fixture, ConsolidationReportStatus.Applying);
        var finishedJob = await AddReportAsync(fixture, ConsolidationReportStatus.Applying);
        var pending = await AddReportAsync(fixture, ConsolidationReportStatus.Pending);
        fixture.Db.Jobs.AddRange(
            Status(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: resumable.Id)), JobStatus.Running),
            MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: queued.Id)),
            Status(MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false, ReportId: finishedJob.Id)), JobStatus.Done),
            MakeJob(new ReconsolidateMemoryPayload(_projectId, DryRun: false)));
        await fixture.Db.SaveChangesAsync();

        var failed = await ReconsolidateMemoryJobHandler.FailOrphanedAppliesAsync(fixture.Memories, new JobRepository(fixture.Db), CancellationToken.None);

        failed.Should().Be(2);
        var statuses = (await fixture.Memories.ListReportsAsync(_projectId, CancellationToken.None)).ToDictionary(r => r.Id, r => r.Status);
        statuses.Should().BeEquivalentTo(new Dictionary<Guid, ConsolidationReportStatus>
        {
            [resumable.Id] = ConsolidationReportStatus.Applying,
            [queued.Id] = ConsolidationReportStatus.Applying,
            [orphaned.Id] = ConsolidationReportStatus.Failed,
            [finishedJob.Id] = ConsolidationReportStatus.Failed,
            [pending.Id] = ConsolidationReportStatus.Pending,
        });

        static Job Status(Job job, JobStatus status)
        {
            job.Status = status;
            return job;
        }
    }

    private Task<MemoryConsolidationReport> AddReportAsync(Fixture fixture, ConsolidationReportStatus status) =>
        fixture.Memories.AddReportAsync(
            new MemoryConsolidationReport
            {
                ProjectId = _projectId,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = status,
                ReportJson = new ReconsolidationReport([], [], 0, new ReconsolidationStats(0, 0, 0, 0, 0, 0, 0, 0, false)).ToJson(),
            },
            CancellationToken.None);

    private async Task<MemoryConsolidationReport> ClaimPendingReportAsync(Fixture fixture)
    {
        var pending = (await fixture.Memories.ListReportsAsync(_projectId, CancellationToken.None)).Single();
        (await fixture.Memories.SetReportStatusAsync(pending.Id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Applying, null, null, CancellationToken.None))
            .Should().BeTrue();
        return pending;
    }

    private static void AssertMerged(IReadOnlyList<DomainMemory> live, IReadOnlyList<DomainMemory> all, DomainMemory first, DomainMemory second)
    {
        var canonical = live.Should().ContainSingle().Subject;
        JsonSerializer.Deserialize<List<string>>(canonical.FactsJson!).Should()
            .Contain(JsonSerializer.Deserialize<List<string>>(first.FactsJson!)!)
            .And.Contain(JsonSerializer.Deserialize<List<string>>(second.FactsJson!)!);
        all.Where(m => m.Id == first.Id || m.Id == second.Id).Should()
            .HaveCount(2).And.OnlyContain(m => m.Status == MemoryStatus.Superseded && m.SupersededById == canonical.Id);
    }

    private async Task<List<DomainMemory>> LiveAsync(Fixture fixture)
    {
        var memories = await fixture.Db.Memories.AsNoTracking().Where(m => m.ProjectId == _projectId).ToListAsync();
        return [.. memories.Where(m => m.IsLive)];
    }

    private async Task<(DomainMemory First, DomainMemory Second)> SeedDuplicatesAsync(Fixture fixture)
    {
        var first = await InsertAsync(fixture, "RefundPolicy allows refunds within 30 days",
            "RefundPolicy.cs allows refunds within 30 days of capture.", ["Refunds are allowed within 30 days of capture"], DateTimeOffset.UtcNow.AddHours(-2));
        var second = await InsertAsync(fixture, "Refund window of RefundPolicy",
            "RefundPolicy.cs checks the refund window against the capture date.", ["The refund window is measured from the capture date"], DateTimeOffset.UtcNow.AddHours(-1));
        return (first, second);
    }

    private async Task<DomainMemory> InsertAsync(Fixture fixture, string title, string body, IReadOnlyList<string> facts, DateTimeOffset at)
    {
        var memory = new DomainMemory
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            Type = MemoryType.Behavior,
            Title = title,
            Body = body,
            Topic = "Refund policy",
            TopicKey = TopicNormalizer.Normalize("Refund policy"),
            FactsJson = JsonSerializer.Serialize(facts),
            CreatedAt = at,
            LastReinforcedAt = at,
        };
        var evidence = new MemoryEvidence
        {
            Id = Guid.NewGuid(),
            ProjectId = _projectId,
            MemoryId = memory.Id,
            Relation = EvidenceRelation.Created,
            Source = EvidenceSource.EventBatch,
            SessionId = Guid.NewGuid(),
            ObservedAt = at,
        };
        ConfidenceModel.Recompute(memory, [evidence]);
        var entities = EntityExtractor.Extract(memory)
            .Select(e => new MemoryEntity { ProjectId = _projectId, MemoryId = memory.Id, Key = e.Key, Display = e.Display })
            .ToList();
        await fixture.Store.InsertAsync(memory, new MemoryVector("unknown", [1f, 0f]), entities, [evidence], CancellationToken.None);
        return memory;
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        var db = new ContextDbContext(Options);
        await db.Database.MigrateAsync();
        db.Projects.Add(new Project { Id = _projectId, Name = "Payments", RootPath = "/repo/payments", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var vectorIndex = new SqliteMemoryVectorIndex(_dbPath, new FakeDbContextFactory(Options));
        var memories = new MemoryRepository(db);
        var store = new MemoryStoreAdapter(memories, db, vectorIndex);
        var codeVerifier = new CodeVerifier(new FileRepository(db), new SymbolRepository(db), new ProjectRepository(db));
        var embeddings = new FixedEmbeddingGenerator();
        var reconsolidator = new MemoryReconsolidator(store, codeVerifier, new FakeProviderResolver(embeddings), new FakeUsageRecorder(), new FixedMergeOptionsProvider());
        return new Fixture(db, vectorIndex, memories, store, embeddings, new ReconsolidateMemoryJobHandler(reconsolidator, memories));
    }

    private Job MakeJob(ReconsolidateMemoryPayload payload, int attempts = 0) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ReconsolidateMemory,
        ProjectId = _projectId,
        PayloadJson = JsonSerializer.Serialize(payload),
        Attempts = attempts,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed record Fixture(
        ContextDbContext Db,
        SqliteMemoryVectorIndex VectorIndex,
        MemoryRepository Memories,
        MemoryStoreAdapter Store,
        FixedEmbeddingGenerator Embeddings,
        ReconsolidateMemoryJobHandler Sut) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            VectorIndex.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class FakeProviderResolver(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : IProviderResolver
    {
        private readonly IChatClient _chatClient = new NoOpChatClient();

        public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(embeddingGenerator);

        public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(_chatClient);
    }

    private sealed class NoOpChatClient : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("noop", null, null);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("The offline plan must not call the chat model.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? Metadata : null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public bool Fail { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
            Fail
                ? throw new InvalidOperationException("The embedding provider is down.")
                : Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1f, 0f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeUsageRecorder : IUsageRecorder
    {
        public Task RecordAsync(UsageRecord record, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeDbContextFactory(DbContextOptions<ContextDbContext> options) : IDbContextFactory<ContextDbContext>
    {
        public ContextDbContext CreateDbContext() => new(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
