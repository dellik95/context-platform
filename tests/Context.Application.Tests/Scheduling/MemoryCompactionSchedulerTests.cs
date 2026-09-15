using Context.Application.Jobs;
using Context.Application.Scheduling;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Context.Application.Tests.Scheduling;

public class MemoryCompactionSchedulerTests
{
    private readonly Mock<IProjectRepository> _projectRepository = new();
    private readonly Mock<IProjectSettingsService> _settingsService = new();
    private readonly Mock<IJobQueue> _jobQueue = new();
    private readonly Guid _projectId = Guid.NewGuid();

    private MemoryCompactionScheduler CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_projectRepository.Object);
        services.AddSingleton(_settingsService.Object);
        services.AddSingleton(_jobQueue.Object);
        var provider = services.BuildServiceProvider();
        return new MemoryCompactionScheduler(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static ProjectSettingsDto SettingsWithCompaction(int intervalMinutes, DateTimeOffset? lastScheduledAt) => new(
        EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null,
        OllamaChatModel: null, ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null,
        MemoryCompactionIntervalMinutes: intervalMinutes, LastMemoryCompactionScheduledAt: lastScheduledAt);

    [Fact]
    public async Task PollOnceAsync_enqueues_a_compaction_job_and_stamps_the_schedule_when_due()
    {
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SettingsWithCompaction(60, lastScheduledAt: null));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _jobQueue.Verify(q => q.EnqueueAsync(
            JobType.CompactMemory,
            It.Is<object>(p => ((CompactMemoryPayload)p).ProjectId == _projectId),
            _projectId,
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _settingsService.Verify(s => s.UpdateAsync(
            _projectId,
            It.Is<ProjectSettingsDto>(d => d.LastMemoryCompactionScheduledAt != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnceAsync_never_enqueues_when_compaction_is_disabled()
    {
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SettingsWithCompaction(0, lastScheduledAt: null));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _settingsService.Verify(s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<ProjectSettingsDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnceAsync_never_enqueues_when_the_interval_has_not_yet_elapsed()
    {
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SettingsWithCompaction(60, lastScheduledAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _jobQueue.Verify(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _settingsService.Verify(s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<ProjectSettingsDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
