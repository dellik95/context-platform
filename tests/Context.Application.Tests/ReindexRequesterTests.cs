using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Moq;

namespace Context.Application.Tests;

public sealed class ReindexRequesterTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<IJobRepository> _jobs = new();

    private ReindexRequester CreateSut(bool active)
    {
        _jobs.Setup(j => j.ListActiveByTypeAsync(JobType.ReindexProject, _projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(active ? [new ActiveJob(Guid.NewGuid(), DateTimeOffset.UtcNow)] : (IReadOnlyList<ActiveJob>)[]);
        _queue.Setup(q => q.EnqueueAsync(JobType.ReindexProject, It.IsAny<object>(), _projectId, JobPriority.Session, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        return new ReindexRequester(_queue.Object, _jobs.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestAsync_queues_a_reindex_job_when_none_is_active(bool full)
    {
        var jobId = await CreateSut(active: false).RequestAsync(_projectId, full, skipIfActive: true, CancellationToken.None);

        jobId.Should().NotBeNull();
        _queue.Verify(
            q => q.EnqueueAsync(
                JobType.ReindexProject,
                It.Is<object>(p => p is ReindexProjectPayload && ((ReindexProjectPayload)p).ProjectId == _projectId && ((ReindexProjectPayload)p).Full == full),
                _projectId, JobPriority.Session, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestAsync_leaves_an_active_reindex_alone_when_asked_to_skip()
    {
        var jobId = await CreateSut(active: true).RequestAsync(_projectId, full: false, skipIfActive: true, CancellationToken.None);

        jobId.Should().BeNull();
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RequestAsync_always_queues_an_explicit_request_so_it_replaces_a_running_pass()
    {
        var jobId = await CreateSut(active: true).RequestAsync(_projectId, full: true, skipIfActive: false, CancellationToken.None);

        jobId.Should().NotBeNull();
        _jobs.Verify(j => j.ListActiveByTypeAsync(It.IsAny<JobType>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
