using System.Text.Json;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using FluentAssertions;
using Moq;

namespace Context.Application.Tests;

public sealed class ForgetMemoryJobHandlerTests
{
    private readonly Mock<IContextEngine> _engine = new();
    private readonly Mock<IProjectActivityNotifier> _notifier = new();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _memoryId = Guid.NewGuid();

    private Job MakeJob(Guid? projectId) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ForgetMemory,
        ProjectId = projectId,
        PayloadJson = JsonSerializer.Serialize(new ForgetMemoryPayload(_memoryId)),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleAsync_deletes_the_memory_and_treats_an_already_deleted_one_as_success(bool existed)
    {
        _engine.Setup(e => e.ForgetMemoryAsync(_projectId, _memoryId, It.IsAny<CancellationToken>())).ReturnsAsync(existed);
        var sut = new ForgetMemoryJobHandler(_engine.Object, _notifier.Object);

        await sut.HandleAsync(MakeJob(_projectId), CancellationToken.None);

        _engine.Verify(e => e.ForgetMemoryAsync(_projectId, _memoryId, It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.NotifyAsync(_projectId, ProjectActivityKind.MemoryChanged, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_lets_a_failure_fail_the_job_so_it_is_retried()
    {
        _engine.Setup(e => e.ForgetMemoryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var sut = new ForgetMemoryJobHandler(_engine.Object, _notifier.Object);

        var act = () => sut.HandleAsync(MakeJob(_projectId), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<ProjectActivityKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_does_not_fail_after_deleting_the_memory_when_the_dashboard_push_fails()
    {
        _engine.Setup(e => e.ForgetMemoryAsync(_projectId, _memoryId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<ProjectActivityKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));
        var sut = new ForgetMemoryJobHandler(_engine.Object, _notifier.Object);

        await sut.HandleAsync(MakeJob(_projectId), CancellationToken.None);

        _engine.Verify(e => e.ForgetMemoryAsync(_projectId, _memoryId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_rejects_a_job_without_a_project()
    {
        var sut = new ForgetMemoryJobHandler(_engine.Object, _notifier.Object);

        var act = () => sut.HandleAsync(MakeJob(null), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
