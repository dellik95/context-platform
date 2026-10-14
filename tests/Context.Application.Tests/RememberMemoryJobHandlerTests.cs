using System.Text.Json;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using FluentAssertions;
using Moq;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

public sealed class RememberMemoryJobHandlerTests
{
    private readonly Mock<IContextEngine> _engine = new();
    private readonly Mock<IProjectActivityNotifier> _notifier = new();
    private readonly Guid _projectId = Guid.NewGuid();

    private static Job MakeJob(Guid? projectId, object payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.RememberMemory,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(payload),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task HandleAsync_stores_the_memory_with_every_field_from_the_payload_and_announces_it()
    {
        var payload = new RememberMemoryPayload(
            "Use SQLite for the cache.", MemoryType.Decision, "Cache", "Caching",
            ["SQLite"], ["why-it-exists"], ["src/A.cs"], ["src/B.cs"]);
        var sut = new RememberMemoryJobHandler(_engine.Object, _notifier.Object);

        await sut.HandleAsync(MakeJob(_projectId, payload), CancellationToken.None);

        _engine.Verify(
            e => e.RememberExplicitlyAsync(
                _projectId, "Use SQLite for the cache.", MemoryType.Decision, It.IsAny<CancellationToken>(),
                "Cache", "Caching",
                It.Is<IReadOnlyList<string>>(f => f.SequenceEqual(new[] { "SQLite" })),
                It.Is<IReadOnlyList<string>>(c => c.SequenceEqual(new[] { "why-it-exists" })),
                It.Is<IReadOnlyList<string>>(r => r.SequenceEqual(new[] { "src/A.cs" })),
                It.Is<IReadOnlyList<string>>(m => m.SequenceEqual(new[] { "src/B.cs" }))),
            Times.Once);
        _notifier.Verify(n => n.NotifyAsync(_projectId, ProjectActivityKind.MemoryChanged, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_lets_a_provider_failure_fail_the_job_so_it_is_retried_and_announces_nothing()
    {
        _engine
            .Setup(e => e.RememberExplicitlyAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<MemoryType?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<IReadOnlyList<string>?>()))
            .ThrowsAsync(new HttpRequestException("embedding provider unreachable"));
        var sut = new RememberMemoryJobHandler(_engine.Object, _notifier.Object);

        var act = () => sut.HandleAsync(MakeJob(_projectId, new RememberMemoryPayload("Anything durable.")), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<ProjectActivityKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_rejects_a_job_without_a_project()
    {
        var sut = new RememberMemoryJobHandler(_engine.Object, _notifier.Object);

        var act = () => sut.HandleAsync(MakeJob(null, new RememberMemoryPayload("Anything durable.")), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
