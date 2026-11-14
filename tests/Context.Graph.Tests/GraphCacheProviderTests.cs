using Context.Domain.Entities;
using Context.Graph.Abstractions;
using FluentAssertions;

namespace Context.Graph.Tests;

public sealed class GraphCacheProviderTests
{
    [Fact]
    public async Task GetOrCreateAsync_CallerCancellation_DoesNotAbortOrPoisonConcurrentWaiters()
    {
        var projectId = Guid.NewGuid();
        var persistenceRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new GatedGraphPersistence(persistenceRelease.Task);
        var provider = new GraphCacheProvider(persistence);

        using var cancelledCts = new CancellationTokenSource();
        var waiterA = provider.GetOrCreateAsync(projectId, cancelledCts.Token);
        var waiterB = provider.GetOrCreateAsync(projectId, CancellationToken.None);

        cancelledCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterA);

        persistenceRelease.SetResult();
        var cacheB = await waiterB;

        cacheB.ProjectId.Should().Be(projectId);
        provider.TryGetLoaded(projectId, out var loaded).Should().BeTrue();
        loaded.Should().BeSameAs(cacheB);
        persistence.LoadCalls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrCreateAsync_FailedLoad_EvictsSlotSoNextCallerRetries()
    {
        var projectId = Guid.NewGuid();
        var persistence = new FlakyGraphPersistence();
        var provider = new GraphCacheProvider(persistence);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetOrCreateAsync(projectId, CancellationToken.None));
        provider.TryGetLoaded(projectId, out _).Should().BeFalse();

        var cache = await provider.GetOrCreateAsync(projectId, CancellationToken.None);

        cache.ProjectId.Should().Be(projectId);
        provider.TryGetLoaded(projectId, out var loaded).Should().BeTrue();
        loaded.Should().BeSameAs(cache);
        persistence.Attempts.Should().Be(2);
    }

    private sealed class GatedGraphPersistence(Task gate) : IGraphPersistence
    {
        public int LoadCalls { get; private set; }

        public async Task<GraphSnapshot> GetAllAsync(Guid projectId, CancellationToken ct)
        {
            LoadCalls++;
            await gate.WaitAsync(ct);
            return new GraphSnapshot([], []);
        }

        public Task AddNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken ct) => Task.CompletedTask;
        public Task AddEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct) => Task.CompletedTask;
        public Task ReplaceForFileAsync(Guid projectId, Guid fileId, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FlakyGraphPersistence : IGraphPersistence
    {
        public int Attempts { get; private set; }

        public Task<GraphSnapshot> GetAllAsync(Guid projectId, CancellationToken ct)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new InvalidOperationException("transient load failure");
            }

            return Task.FromResult(new GraphSnapshot([], []));
        }

        public Task AddNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken ct) => Task.CompletedTask;
        public Task AddEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct) => Task.CompletedTask;
        public Task ReplaceForFileAsync(Guid projectId, Guid fileId, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct) => Task.CompletedTask;
    }
}
