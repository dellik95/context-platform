using Context.Application.Jobs;
using Context.Application.Scheduling;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests.Scheduling;

public class MemoryReconsolidationSchedulerTests
{
    private readonly Mock<IProjectRepository> _projectRepository = new();
    private readonly Mock<IProjectSettingsService> _settingsService = new();
    private readonly Mock<IMemoryRepository> _memoryRepository = new();
    private readonly Mock<IJobQueue> _jobQueue = new();
    private readonly Guid _projectId = Guid.NewGuid();

    public MemoryReconsolidationSchedulerTests()
    {
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
    }

    private MemoryReconsolidationScheduler CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_projectRepository.Object);
        services.AddSingleton(_settingsService.Object);
        services.AddSingleton(_memoryRepository.Object);
        services.AddSingleton(_jobQueue.Object);
        var provider = services.BuildServiceProvider();
        return new MemoryReconsolidationScheduler(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private void Settings(int intervalMinutes, DateTimeOffset? lastScheduledAt) =>
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new ProjectSettingsDto(
            EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null,
            OllamaChatModel: null, ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null,
            MemoryReconsolidationIntervalMinutes: intervalMinutes, LastMemoryReconsolidationScheduledAt: lastScheduledAt));

    private void LiveMemories(params (MemoryType Type, DateTimeOffset LastReinforcedAt)[] memories) =>
        _memoryRepository.Setup(r => r.ListByProjectAsync(_projectId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. memories.Select(m => new DomainMemory
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                Type = m.Type,
                Title = "t",
                Body = "b",
                CreatedAt = m.LastReinforcedAt,
                LastReinforcedAt = m.LastReinforcedAt,
            })]);

    private void VerifyStamped(Times times) =>
        _settingsService.Verify(
            s => s.UpdateAsync(_projectId, It.Is<ProjectSettingsDto>(d => d.LastMemoryReconsolidationScheduledAt != null), It.IsAny<CancellationToken>()),
            times);

    private void VerifyEnqueued(Times times) =>
        _jobQueue.Verify(
            q => q.EnqueueAsync(
                JobType.ReconsolidateMemory,
                It.Is<object>(p => p is ReconsolidateMemoryPayload && ((ReconsolidateMemoryPayload)p).ProjectId == _projectId && !((ReconsolidateMemoryPayload)p).DryRun),
                _projectId,
                JobPriority.Session,
                It.IsAny<CancellationToken>()),
            times);

    [Fact]
    public async Task PollOnceAsync_does_nothing_when_the_interval_is_disabled()
    {
        Settings(0, null);
        LiveMemories((MemoryType.Fact, DateTimeOffset.UtcNow));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        VerifyStamped(Times.Never());
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task PollOnceAsync_does_nothing_before_the_interval_has_elapsed()
    {
        Settings(60, DateTimeOffset.UtcNow.AddMinutes(-10));
        LiveMemories((MemoryType.Fact, DateTimeOffset.UtcNow));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        VerifyStamped(Times.Never());
        VerifyEnqueued(Times.Never());
    }

    [Fact]
    public async Task PollOnceAsync_stamps_and_enqueues_an_applying_background_run_when_knowledge_changed_since_the_last_run()
    {
        var last = DateTimeOffset.UtcNow.AddHours(-2);
        Settings(60, last);
        LiveMemories((MemoryType.Fact, last.AddMinutes(-30)), (MemoryType.Behavior, last.AddMinutes(30)));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        VerifyStamped(Times.Once());
        VerifyEnqueued(Times.Once());
    }

    [Fact]
    public async Task PollOnceAsync_enqueues_the_first_run_of_a_project_with_knowledge()
    {
        Settings(60, null);
        LiveMemories((MemoryType.Fact, DateTimeOffset.UtcNow.AddDays(-30)));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        VerifyStamped(Times.Once());
        VerifyEnqueued(Times.Once());
    }
}
