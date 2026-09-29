using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Repositories;
using Context.Memory.Abstractions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

public sealed class MemoryStoreAdapterTests : IDisposable
{
    private const string ModelKey = "fake:embed";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-memorystore-tests-{Guid.NewGuid():N}.db");
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly Guid _otherSessionId = Guid.NewGuid();
    private readonly Mock<IMemoryVectorIndex> _vectorIndex = new();

    private DbContextOptions<ContextDbContext> Options =>
        new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options;

    [Fact]
    public async Task InsertAsync_writes_the_memory_with_its_entities_evidence_and_embedding_and_indexes_the_vector()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Webhook retries", topic: "Payments webhooks");
        float[] vector = [1f, 0f];

        await sut.InsertAsync(
            memory,
            new MemoryVector(ModelKey, vector),
            [Entity("symbol:webhookclient", "WebhookClient"), Entity("symbol:webhookclient", "webhookclient"), Entity("file:retry.cs", "Retry.cs")],
            [new MemoryEvidence { ProjectId = _projectId, MemoryId = memory.Id, Relation = EvidenceRelation.Created, Source = EvidenceSource.EventBatch, SessionId = _sessionId, ObservedAt = DateTimeOffset.UtcNow }],
            CancellationToken.None);

        await using var verify = new ContextDbContext(Options);
        (await verify.Memories.SingleAsync()).Title.Should().Be("Webhook retries");
        var entities = await verify.MemoryEntities.ToListAsync();
        entities.Select(e => e.Key).Should().BeEquivalentTo(["symbol:webhookclient", "file:retry.cs"]);
        entities.Should().OnlyContain(e => e.MemoryId == memory.Id && e.ProjectId == _projectId);
        (await verify.MemoryEvidence.SingleAsync()).Id.Should().NotBeEmpty();

        var embeddings = await sut.GetEmbeddingsAsync([memory.Id], CancellationToken.None);
        embeddings[memory.Id].ModelKey.Should().Be(ModelKey);
        embeddings[memory.Id].Vector.Should().Equal(vector);
        _vectorIndex.Verify(v => v.UpsertAsync(memory.Id, _projectId, "Payments webhooks", ModelKey, vector, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InsertAsync_of_a_non_live_memory_keeps_the_embedding_row_but_skips_the_vector_index()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Old retry policy", status: MemoryStatus.Superseded);

        await sut.InsertAsync(memory, new MemoryVector(ModelKey, [1f, 0f]), [], [], CancellationToken.None);

        (await sut.GetEmbeddingsAsync([memory.Id], CancellationToken.None)).Should().ContainKey(memory.Id);
        _vectorIndex.Verify(
            v => v.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_replaces_the_entity_set_appends_evidence_and_re_embeds()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Webhook retries");
        await sut.InsertAsync(
            memory,
            new MemoryVector(ModelKey, [1f, 0f]),
            [Entity("symbol:webhookclient", "WebhookClient"), Entity("file:retry.cs", "retry.cs")],
            [Evidence(memory.Id, EvidenceRelation.Created)],
            CancellationToken.None);

        memory.Body = "WebhookClient retries five times with a 30s timeout.";
        memory.ReinforcementCount = 1;
        float[] reembedded = [0f, 1f];
        await sut.UpdateAsync(
            memory,
            new MemoryVector(ModelKey, reembedded),
            [Entity("file:retry.cs", "Retry.cs"), Entity("config:webhook_timeout", "WEBHOOK_TIMEOUT")],
            [Evidence(memory.Id, EvidenceRelation.Merged)],
            CancellationToken.None);

        await using var verify = new ContextDbContext(Options);
        var stored = await verify.Memories.SingleAsync();
        stored.Body.Should().Contain("30s timeout");
        stored.ReinforcementCount.Should().Be(1);
        var entities = await verify.MemoryEntities.ToListAsync();
        entities.Select(e => (e.Key, e.Display)).Should().BeEquivalentTo([("file:retry.cs", "Retry.cs"), ("config:webhook_timeout", "WEBHOOK_TIMEOUT")]);
        (await verify.MemoryEvidence.Select(e => e.Relation).ToListAsync()).Should().BeEquivalentTo([EvidenceRelation.Created, EvidenceRelation.Merged]);
        (await sut.GetEmbeddingsAsync([memory.Id], CancellationToken.None))[memory.Id].Vector.Should().Equal(reembedded);
        _vectorIndex.Verify(v => v.UpsertAsync(memory.Id, _projectId, null, ModelKey, reembedded, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_re_indexes_the_stored_vector_when_only_the_topic_changes()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Webhook retries", topic: "Webhooks");
        float[] vector = [1f, 0f];
        await sut.InsertAsync(memory, new MemoryVector(ModelKey, vector), [Entity("file:retry.cs", "retry.cs")], [], CancellationToken.None);

        memory.Topic = "Payments webhooks";
        await sut.UpdateAsync(memory, reembedding: null, entities: null, [], CancellationToken.None);

        (await db.MemoryEntities.AsNoTracking().CountAsync()).Should().Be(1);
        _vectorIndex.Verify(
            v => v.UpsertAsync(memory.Id, _projectId, "Payments webhooks", ModelKey, It.Is<float[]>(f => f.SequenceEqual(vector)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_of_an_unknown_memory_throws()
    {
        await using var db = await CreateDbAsync();

        var act = () => CreateSut(db).UpdateAsync(NewMemory("Missing"), null, null, [], CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task ListLiveEventsBySessionAsync_returns_only_live_events_of_that_session()
    {
        await using var db = await CreateDbAsync();
        var liveEvent = NewMemory("Deployed build 42", type: MemoryType.Event);
        var obsoleteEvent = NewMemory("Deployed build 41", type: MemoryType.Event, status: MemoryStatus.Obsolete);
        var otherSessionEvent = NewMemory("Deployed build 40", type: MemoryType.Event, session: _otherSessionId);
        var knowledge = NewMemory("Deploys run nightly");
        db.Memories.AddRange(liveEvent, obsoleteEvent, otherSessionEvent, knowledge);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var events = await CreateSut(db).ListLiveEventsBySessionAsync(_projectId, _sessionId, CancellationToken.None);

        events.Select(m => m.Id).Should().Equal(liveEvent.Id);
    }

    [Fact]
    public async Task UpdateAsync_refuses_to_write_a_memory_that_is_no_longer_live()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Old retry policy", status: MemoryStatus.Superseded);
        await sut.InsertAsync(memory, null, [], [], CancellationToken.None);

        memory.Status = MemoryStatus.Active;
        var act = () => sut.UpdateAsync(memory, null, null, [], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await sut.GetByIdsAsync([memory.Id], CancellationToken.None)).Single().Status.Should().Be(MemoryStatus.Superseded);
    }

    [Fact]
    public async Task FindLiveKnowledgeEntityMatchesAsync_skips_events_and_memories_that_are_no_longer_live()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var live = NewMemory("Webhook retries");
        var superseded = NewMemory("Old webhook retries", status: MemoryStatus.Superseded);
        var liveEvent = NewMemory("Fixed the webhook retry bug", type: MemoryType.Event);
        foreach (var memory in new[] { live, superseded, liveEvent })
        {
            await sut.InsertAsync(memory, null, [Entity("symbol:webhookclient", "WebhookClient")], [], CancellationToken.None);
        }

        var matches = await sut.FindLiveKnowledgeEntityMatchesAsync(_projectId, ["symbol:webhookclient"], CancellationToken.None);

        matches.Select(e => e.MemoryId).Should().Equal(live.Id);
    }

    [Fact]
    public async Task SupersedeAsync_marks_only_a_live_memory_superseded_and_drops_its_vector()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var old = NewMemory("Retries three times");
        var replacement = NewMemory("Retries five times");
        await sut.InsertAsync(old, null, [], [], CancellationToken.None);
        await sut.InsertAsync(replacement, null, [], [], CancellationToken.None);
        var at = DateTimeOffset.UtcNow;

        (await sut.SupersedeAsync(old.Id, replacement.Id, at, CancellationToken.None)).Should().BeTrue();
        (await sut.SupersedeAsync(old.Id, Guid.NewGuid(), at, CancellationToken.None)).Should().BeFalse("it already left the live set");
        (await sut.SupersedeAsync(Guid.NewGuid(), replacement.Id, at, CancellationToken.None)).Should().BeFalse();

        var stored = (await sut.GetByIdsAsync([old.Id], CancellationToken.None)).Single();
        stored.Status.Should().Be(MemoryStatus.Superseded);
        stored.SupersededById.Should().Be(replacement.Id);
        stored.SupersededAt.Should().BeCloseTo(at, TimeSpan.FromSeconds(1));
        _vectorIndex.Verify(v => v.DeleteAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == old.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ObsoleteAsync_retires_only_a_live_memory_and_drops_its_vector()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Deploys run nightly", status: MemoryStatus.Contested);
        await sut.InsertAsync(memory, null, [], [], CancellationToken.None);

        (await sut.ObsoleteAsync(memory.Id, CancellationToken.None)).Should().BeTrue();
        (await sut.ObsoleteAsync(memory.Id, CancellationToken.None)).Should().BeFalse();

        (await sut.GetByIdsAsync([memory.Id], CancellationToken.None)).Single().Status.Should().Be(MemoryStatus.Obsolete);
        _vectorIndex.Verify(v => v.DeleteAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InTransactionAsync_writes_vectors_only_after_the_commit()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Webhook retries");
        var vectorWrittenBeforeCommit = false;

        await sut.InTransactionAsync(async () =>
        {
            await sut.InsertAsync(memory, new MemoryVector(ModelKey, [1f, 0f]), [], [], CancellationToken.None);
            await sut.InTransactionAsync(() => sut.ObsoleteAsync(memory.Id, CancellationToken.None), CancellationToken.None);
            vectorWrittenBeforeCommit = _vectorIndex.Invocations.Count > 0;
            return true;
        }, CancellationToken.None);

        vectorWrittenBeforeCommit.Should().BeFalse();
        var sequence = _vectorIndex.Invocations.Select(i => i.Method.Name).ToList();
        sequence.Should().Equal(nameof(IMemoryVectorIndex.UpsertAsync), nameof(IMemoryVectorIndex.DeleteAsync));
        (await sut.GetByIdsAsync([memory.Id], CancellationToken.None)).Single().Status.Should().Be(MemoryStatus.Obsolete);
    }

    [Fact]
    public async Task InTransactionAsync_rolls_back_rows_and_skips_vector_writes_when_the_work_throws()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var memory = NewMemory("Webhook retries");

        var act = () => sut.InTransactionAsync<bool>(async () =>
        {
            await sut.InsertAsync(memory, new MemoryVector(ModelKey, [1f, 0f]), [], [], CancellationToken.None);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await sut.GetByIdsAsync([memory.Id], CancellationToken.None)).Should().BeEmpty();
        _vectorIndex.Invocations.Should().BeEmpty();

        await sut.InsertAsync(NewMemory("Deploys run nightly"), new MemoryVector(ModelKey, [0f, 1f]), [], [], CancellationToken.None);
        _vectorIndex.Invocations.Should().ContainSingle("writes outside a transaction go straight to the index again");
    }

    [Fact]
    public async Task DeleteAsync_removes_the_memory_and_its_rows_obsoletes_what_it_superseded_and_drops_its_vector()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var old = NewMemory("Retries three times");
        var replacement = NewMemory("Retries five times");
        await sut.InsertAsync(old, null, [], [], CancellationToken.None);
        await sut.InsertAsync(
            replacement,
            new MemoryVector(ModelKey, [1f, 0f]),
            [Entity("symbol:webhookclient", "WebhookClient")],
            [Evidence(replacement.Id, EvidenceRelation.Created)],
            CancellationToken.None);
        await sut.SupersedeAsync(old.Id, replacement.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        _vectorIndex.Invocations.Clear();

        (await sut.InTransactionAsync(() => sut.DeleteAsync(replacement.Id, CancellationToken.None), CancellationToken.None)).Should().BeTrue();
        (await sut.DeleteAsync(replacement.Id, CancellationToken.None)).Should().BeFalse();

        await using var verify = new ContextDbContext(Options);
        var kept = await verify.Memories.SingleAsync();
        kept.Id.Should().Be(old.Id);
        kept.Status.Should().Be(MemoryStatus.Obsolete);
        kept.SupersededById.Should().BeNull();
        (await verify.MemoryEvidence.AnyAsync()).Should().BeFalse();
        (await verify.MemoryEntities.AnyAsync()).Should().BeFalse();
        (await verify.MemoryEmbeddings.AnyAsync()).Should().BeFalse();
        _vectorIndex.Verify(v => v.DeleteAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == replacement.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListLiveKnowledgeCreatedSinceAsync_returns_only_live_knowledge_created_at_or_after_the_cutoff()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        var since = DateTimeOffset.UtcNow.AddMinutes(-30);
        var recent = NewMemory("Webhook retries");
        var old = NewMemory("Deploys run nightly");
        old.CreatedAt = since.AddMinutes(-1);
        var retired = NewMemory("Retries three times", status: MemoryStatus.Obsolete);
        var recentEvent = NewMemory("Fixed the retry loop", type: MemoryType.Event);
        foreach (var memory in new[] { recent, old, retired, recentEvent })
        {
            await sut.InsertAsync(memory, null, [], [], CancellationToken.None);
        }

        var found = await sut.ListLiveKnowledgeCreatedSinceAsync(_projectId, since, CancellationToken.None);

        found.Select(m => m.Id).Should().Equal(recent.Id);
    }

    [Fact]
    public async Task A_failed_vector_write_keeps_the_saved_rows_and_does_not_throw()
    {
        await using var db = await CreateDbAsync();
        var sut = CreateSut(db);
        _vectorIndex
            .Setup(v => v.UpsertAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vector store unavailable"));
        var inTransaction = NewMemory("Webhook retries");
        var direct = NewMemory("Deploys run nightly");

        await sut.InTransactionAsync(async () =>
        {
            await sut.InsertAsync(inTransaction, new MemoryVector(ModelKey, [1f, 0f]), [], [], CancellationToken.None);
            return true;
        }, CancellationToken.None);
        await sut.InsertAsync(direct, new MemoryVector(ModelKey, [0f, 1f]), [], [], CancellationToken.None);

        (await sut.GetByIdsAsync([inTransaction.Id, direct.Id], CancellationToken.None)).Should().HaveCount(2);
        (await sut.GetEmbeddingsAsync([inTransaction.Id, direct.Id], CancellationToken.None)).Should().HaveCount(2);
    }

    private MemoryStoreAdapter CreateSut(ContextDbContext db) => new(new MemoryRepository(db), db, _vectorIndex.Object);

    private async Task<ContextDbContext> CreateDbAsync()
    {
        var db = new ContextDbContext(Options);
        await db.Database.MigrateAsync();
        db.Projects.Add(new Project { Id = _projectId, Name = "Payments", RootPath = "/repo/payments", CreatedAt = DateTimeOffset.UtcNow });
        db.Sessions.AddRange(
            new Session { Id = _sessionId, ProjectId = _projectId, StartedAt = DateTimeOffset.UtcNow },
            new Session { Id = _otherSessionId, ProjectId = _projectId, StartedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private DomainMemory NewMemory(
        string title,
        string? topic = null,
        MemoryType type = MemoryType.Fact,
        MemoryStatus status = MemoryStatus.Active,
        Guid? session = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = _projectId,
        Type = type,
        Status = status,
        Title = title,
        Body = title + ".",
        Topic = topic,
        CreatedFromSessionId = session ?? _sessionId,
        CreatedAt = DateTimeOffset.UtcNow,
        LastReinforcedAt = DateTimeOffset.UtcNow,
    };

    private static MemoryEntity Entity(string key, string display) => new() { Key = key, Display = display };

    private MemoryEvidence Evidence(Guid memoryId, EvidenceRelation relation) => new()
    {
        ProjectId = _projectId,
        MemoryId = memoryId,
        Relation = relation,
        Source = EvidenceSource.EventBatch,
        SessionId = _sessionId,
        ObservedAt = DateTimeOffset.UtcNow,
    };

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
