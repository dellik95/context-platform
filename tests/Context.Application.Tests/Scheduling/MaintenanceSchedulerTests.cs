using Context.Application.Jobs;
using Context.Application.Scheduling;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Context.Application.Tests.Scheduling;

public class MaintenanceSchedulerTests
{
    private readonly Mock<IJobRepository> _jobRepository = new();
    private readonly Mock<IJobQueue> _jobQueue = new();

    private MaintenanceScheduler CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_jobRepository.Object);
        services.AddSingleton(_jobQueue.Object);
        var provider = services.BuildServiceProvider();
        return new MaintenanceScheduler(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private void LastEnqueued(JobType type, DateTimeOffset? at) =>
        _jobRepository.Setup(r => r.GetLastEnqueuedAtAsync(type, It.IsAny<CancellationToken>())).ReturnsAsync(at);

    private void VerifyEnqueued(JobType type, Times times) =>
        _jobQueue.Verify(
            q => q.EnqueueAsync(type, It.IsAny<object>(), null, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            times);

    [Fact]
    public async Task PollOnceAsync_enqueues_both_maintenance_jobs_when_neither_has_ever_run()
    {
        LastEnqueued(JobType.BackupDb, null);
        LastEnqueued(JobType.DecayMemory, null);

        await CreateSut().PollOnceAsync(CancellationToken.None);

        VerifyEnqueued(JobType.BackupDb, Times.Once());
        VerifyEnqueued(JobType.DecayMemory, Times.Once());
    }

    [Fact]
    public async Task PollOnceAsync_skips_a_job_that_already_ran_within_the_interval()
    {
        LastEnqueued(JobType.BackupDb, DateTimeOffset.UtcNow.AddHours(-2));
        LastEnqueued(JobType.DecayMemory, DateTimeOffset.UtcNow.AddHours(-2));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        // The hourly poll is deliberately more frequent than the daily cadence; the queue, not the
        // timer, decides whether the work is due.
        VerifyEnqueued(JobType.BackupDb, Times.Never());
        VerifyEnqueued(JobType.DecayMemory, Times.Never());
    }

    [Fact]
    public async Task PollOnceAsync_enqueues_each_job_independently_of_the_other()
    {
        LastEnqueued(JobType.BackupDb, DateTimeOffset.UtcNow.AddHours(-2));
        LastEnqueued(JobType.DecayMemory, DateTimeOffset.UtcNow.AddDays(-3));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        VerifyEnqueued(JobType.BackupDb, Times.Never());
        VerifyEnqueued(JobType.DecayMemory, Times.Once());
    }
}
