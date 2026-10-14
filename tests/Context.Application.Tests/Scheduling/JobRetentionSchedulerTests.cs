using Context.Application.Scheduling;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Context.Application.Tests.Scheduling;

public class JobRetentionSchedulerTests
{
    private readonly Mock<IJobRepository> _jobs = new();

    private JobRetentionScheduler CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_jobs.Object);
        return new JobRetentionScheduler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task PollOnceAsync_prunes_done_hook_jobs_after_a_day_and_everything_else_after_a_week()
    {
        await CreateSut().PollOnceAsync(CancellationToken.None);

        _jobs.Verify(
            r => r.DeleteFinishedOlderThanAsync(
                It.Is<DateTimeOffset>(c => c <= DateTimeOffset.UtcNow.AddDays(-1) && c > DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-1)),
                It.Is<JobRetentionFilter>(f => f.OnlyType == JobType.ProcessHook && f.OnlyStatus == JobStatus.Done),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _jobs.Verify(
            r => r.DeleteFinishedOlderThanAsync(
                It.Is<DateTimeOffset>(c => c <= DateTimeOffset.UtcNow.AddDays(-7) && c > DateTimeOffset.UtcNow.AddDays(-7).AddMinutes(-1)),
                JobRetentionFilter.All,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
