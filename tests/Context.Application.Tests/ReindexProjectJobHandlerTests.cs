using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using FluentAssertions;
using Moq;

namespace Context.Application.Tests;

public sealed class ReindexProjectJobHandlerTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Mock<IIndexingPipeline> _pipeline = new();
    private readonly Mock<IIndexingProgressReporter> _progress = new();
    private readonly Mock<IProjectStatusPublisher> _status = new();
    private readonly ReindexCancellationRegistry _registry = new();
    private readonly List<ReindexProgressDto> _reported = [];

    public ReindexProjectJobHandlerTests()
    {
        _progress.Setup(p => p.ReportAsync(It.IsAny<ReindexProgressDto>(), It.IsAny<CancellationToken>()))
            .Callback<ReindexProgressDto, CancellationToken>((dto, _) => _reported.Add(dto))
            .Returns(Task.CompletedTask);
    }

    private ReindexProjectJobHandler CreateSut() => new(_pipeline.Object, _registry, _progress.Object, _status.Object);

    private Job MakeJob(bool full = false) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ReindexProject,
        ProjectId = _projectId,
        PayloadJson = JsonSerializer.Serialize(new ReindexProjectPayload(_projectId, full)),
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_completed_pass_runs_the_pipeline_publishes_the_status_and_releases_the_project(bool full)
    {
        await CreateSut().HandleAsync(MakeJob(full), CancellationToken.None);

        _pipeline.Verify(p => p.ReindexProjectAsync(_projectId, full, It.IsAny<CancellationToken>()), Times.Once);
        _status.Verify(s => s.PublishAsync(_projectId, It.IsAny<CancellationToken>()), Times.Once);
        _reported.Should().BeEmpty("the pipeline itself reports a completed pass");
        _registry.TryBegin(_projectId, out _).Should().BeTrue("the project is no longer marked as reindexing");
    }

    [Fact]
    public async Task A_pass_the_user_cancelled_ends_the_job_normally_with_a_cancelled_progress_event()
    {
        _pipeline.Setup(p => p.ReindexProjectAsync(_projectId, false, It.IsAny<CancellationToken>()))
            .Returns((Guid _, bool _, CancellationToken token) =>
            {
                _registry.Cancel(_projectId);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        _reported.Should().ContainSingle().Which.Should().Match<ReindexProgressDto>(p => p.Completed && p.Cancelled && p.ProjectId == _projectId);
        _status.Verify(s => s.PublishAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _registry.TryBegin(_projectId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_host_shutdown_mid_pass_requeues_the_job_instead_of_finishing_it()
    {
        using var shutdown = new CancellationTokenSource();
        _pipeline.Setup(p => p.ReindexProjectAsync(_projectId, false, It.IsAny<CancellationToken>()))
            .Returns((Guid _, bool _, CancellationToken token) =>
            {
                shutdown.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        var act = () => CreateSut().HandleAsync(MakeJob(), shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _reported.Should().BeEmpty();
        _registry.TryBegin(_projectId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_pass_reports_the_failure_and_rethrows_so_the_job_is_retried()
    {
        _pipeline.Setup(p => p.ReindexProjectAsync(_projectId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scan failed"));

        var act = () => CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _reported.Should().ContainSingle().Which.Should().Match<ReindexProgressDto>(p => p.Completed && p.Failed);
        _registry.TryBegin(_projectId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_status_publisher_failure_does_not_fail_a_completed_pass()
    {
        _status.Setup(s => s.PublishAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("hub down"));

        await CreateSut().HandleAsync(MakeJob(), CancellationToken.None);

        _pipeline.Verify(p => p.ReindexProjectAsync(_projectId, false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
