using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Context.Shared;
using Microsoft.Extensions.AI;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Tests;

internal sealed class InMemoryStore : IMemoryStore
{
    private readonly Dictionary<Guid, DomainMemory> _memories = [];
    private readonly Dictionary<Guid, MemoryVector> _vectors = [];
    private readonly List<MemoryEntity> _entities = [];
    private readonly List<MemoryEvidence> _evidence = [];

    public IReadOnlyList<DomainMemory> All => [.. _memories.Values.Select(Clone)];

    public IReadOnlyList<DomainMemory> Live => [.. _memories.Values.Where(m => m.IsLive).Select(Clone)];

    public DomainMemory Get(Guid id) => Clone(_memories[id]);

    public MemoryVector? VectorOf(Guid id) => _vectors.GetValueOrDefault(id);

    public IReadOnlyList<MemoryEvidence> EvidenceFor(Guid id) => [.. _evidence.Where(e => e.MemoryId == id)];

    public int EvidenceCount => _evidence.Count;

    public void Add(DomainMemory memory, MemoryVector? vector, IEnumerable<MemoryEntity> entities, IEnumerable<MemoryEvidence> evidence)
    {
        _memories.Add(memory.Id, Clone(memory));
        if (vector is not null)
        {
            _vectors[memory.Id] = vector;
        }

        _entities.AddRange(entities);
        _evidence.AddRange(evidence);
    }

    public int TransactionCount { get; private set; }

    /// <summary>Runs once as the next transaction opens, standing in for a concurrent writer that committed first.</summary>
    public Action? BeforeNextTransaction { get; set; }

    public Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        TransactionCount++;
        var concurrent = BeforeNextTransaction;
        BeforeNextTransaction = null;
        concurrent?.Invoke();
        return work();
    }

    public Task<IReadOnlyList<DomainMemory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DomainMemory>>([.. ids.Where(_memories.ContainsKey).Select(id => Clone(_memories[id]))]);

    public Task<IReadOnlyList<MemoryEntity>> FindLiveKnowledgeEntityMatchesAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntity>>([.. _entities.Where(e => e.ProjectId == projectId
            && keys.Contains(e.Key)
            && _memories[e.MemoryId].IsLive
            && _memories[e.MemoryId].Type != MemoryType.Event)]);

    public Task<IReadOnlyList<DomainMemory>> ListLiveByTopicKeyAsync(Guid projectId, string topicKey, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DomainMemory>>([.. _memories.Values
            .Where(m => m.ProjectId == projectId && m.IsLive && m.TopicKey == topicKey)
            .Select(Clone)]);

    public Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchSimilarAsync(Guid projectId, MemoryVector query, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(Guid MemoryId, double Score)>>([.. _vectors
            .Where(v => v.Value.ModelKey == query.ModelKey
                && v.Value.Vector.Length == query.Vector.Length
                && _memories[v.Key].IsLive
                && _memories[v.Key].ProjectId == projectId)
            .Select(v => (v.Key, CosineSimilarity.Compute(query.Vector, v.Value.Vector)))
            .OrderByDescending(hit => hit.Item2)
            .Take(limit)]);

    public Task<IReadOnlyDictionary<Guid, MemoryVector>> GetEmbeddingsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, MemoryVector>>(ids.Where(_vectors.ContainsKey).ToDictionary(id => id, id => _vectors[id]));

    public Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEntity>>([.. _entities.Where(e => ids.Contains(e.MemoryId))]);

    public Task<IReadOnlyList<MemoryEvidence>> ListEvidenceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MemoryEvidence>>([.. _evidence.Where(e => ids.Contains(e.MemoryId))]);

    public Task<IReadOnlyList<DomainMemory>> ListLiveEventsBySessionAsync(Guid projectId, Guid sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DomainMemory>>([.. _memories.Values
            .Where(m => m.ProjectId == projectId && m.Type == MemoryType.Event && m.CreatedFromSessionId == sessionId && m.IsLive)
            .Select(Clone)]);

    public Task<IReadOnlyList<DomainMemory>> ListLiveKnowledgeAsync(Guid projectId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DomainMemory>>([.. _memories.Values
            .Where(m => m.ProjectId == projectId && m.Type != MemoryType.Event && m.IsLive)
            .Select(Clone)]);

    public Task<IReadOnlyList<DomainMemory>> ListLiveKnowledgeCreatedSinceAsync(Guid projectId, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DomainMemory>>([.. _memories.Values
            .Where(m => m.ProjectId == projectId && m.Type != MemoryType.Event && m.IsLive && m.CreatedAt >= since)
            .Select(Clone)]);

    public Task InsertAsync(
        DomainMemory memory, MemoryVector? embedding, IReadOnlyCollection<MemoryEntity> entities, IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct)
    {
        Add(memory, embedding, entities, evidence);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(
        DomainMemory memory, MemoryVector? reembedding, IReadOnlyCollection<MemoryEntity>? entities, IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct)
    {
        if (!_memories.TryGetValue(memory.Id, out var stored))
        {
            throw new KeyNotFoundException($"Memory {memory.Id} was not found.");
        }

        if (!stored.IsLive || !memory.IsLive)
        {
            throw new InvalidOperationException($"Memory {memory.Id} is {stored.Status} and cannot be written as {memory.Status}.");
        }

        var becameEvent = _memories[memory.Id].Type != MemoryType.Event && memory.Type == MemoryType.Event;
        _memories[memory.Id] = Clone(memory);
        if (becameEvent)
        {
            _vectors.Remove(memory.Id);
        }
        else if (reembedding is not null)
        {
            _vectors[memory.Id] = reembedding;
        }

        if (entities is not null)
        {
            _entities.RemoveAll(e => e.MemoryId == memory.Id);
            _entities.AddRange(entities);
        }

        _evidence.AddRange(evidence);
        return Task.CompletedTask;
    }

    public Task<bool> SupersedeAsync(Guid oldId, Guid newId, DateTimeOffset at, CancellationToken ct)
    {
        if (!_memories.TryGetValue(oldId, out var old) || !old.IsLive)
        {
            return Task.FromResult(false);
        }

        old.Status = MemoryStatus.Superseded;
        old.SupersededById = newId;
        old.SupersededAt = at;
        return Task.FromResult(true);
    }

    public Task<bool> ObsoleteAsync(Guid id, CancellationToken ct)
    {
        if (!_memories.TryGetValue(id, out var memory) || !memory.IsLive)
        {
            return Task.FromResult(false);
        }

        memory.Status = MemoryStatus.Obsolete;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!_memories.Remove(id))
        {
            return Task.FromResult(false);
        }

        foreach (var superseded in _memories.Values.Where(m => m.SupersededById == id))
        {
            superseded.SupersededById = null;
            superseded.SupersededAt = null;
            superseded.Status = MemoryStatus.Obsolete;
        }

        _vectors.Remove(id);
        _entities.RemoveAll(e => e.MemoryId == id);
        _evidence.RemoveAll(e => e.MemoryId == id);
        return Task.FromResult(true);
    }

    public Task AddEvidenceAsync(IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct)
    {
        _evidence.AddRange(evidence);
        return Task.CompletedTask;
    }

    public Task UpsertEmbeddingAsync(DomainMemory memory, MemoryVector embedding, CancellationToken ct)
    {
        _vectors[memory.Id] = embedding;
        return Task.CompletedTask;
    }

    private static DomainMemory Clone(DomainMemory source)
    {
        var copy = new DomainMemory { Title = source.Title, Body = source.Body };
        foreach (var property in typeof(DomainMemory).GetProperties().Where(p => p.CanWrite))
        {
            property.SetValue(copy, property.GetValue(source));
        }

        return copy;
    }
}

internal sealed class FakeCodeVerifier : ICodeVerifier
{
    public Func<IReadOnlyCollection<string>, IReadOnlyCollection<string>, CodeCheck> Check { get; set; } = (_, _) => CodeCheck.Unknown;

    public List<IReadOnlyCollection<string>> Paths { get; } = [];

    public Task<CodeCheck> CheckAsync(Guid projectId, IReadOnlyCollection<string> paths, IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        Paths.Add(paths);
        return Task.FromResult(Check(paths, symbols));
    }
}

internal sealed class FakeProviderResolver(IChatClient chatClient, IEmbeddingGenerator<string, Embedding<float>> generator) : IProviderResolver
{
    public Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct) => Task.FromResult(chatClient);

    public Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct) =>
        Task.FromResult(generator);
}

/// <summary>Returns the vector of the first mapped phrase the text contains; unmapped text fails the test.</summary>
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private static readonly EmbeddingGeneratorMetadata Metadata = new("fake", null, "embed");
    private readonly List<(string Phrase, float[] Vector)> _map = [];

    public FakeEmbeddingGenerator Map(string phrase, float[] vector)
    {
        _map.Add((phrase, vector));
        return this;
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text => new Embedding<float>(VectorFor(text)))));

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? Metadata : null;

    public void Dispose()
    {
    }

    private float[] VectorFor(string text) =>
        _map.FirstOrDefault(m => text.Contains(m.Phrase, StringComparison.OrdinalIgnoreCase)).Vector
        ?? throw new InvalidOperationException($"No vector mapped for: {text}");
}

internal sealed class FakeUsageRecorder : IUsageRecorder
{
    public List<string> Purposes { get; } = [];

    public Task RecordAsync(UsageRecord record, CancellationToken ct)
    {
        Purposes.Add(record.Purpose);
        return Task.CompletedTask;
    }
}
