using Context.Application.Scheduling;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Context.Application.Tests.Scheduling;

public class HookInvocationRetentionSchedulerTests
{
    private readonly Mock<IProjectRepository> _projectRepository = new();
    private readonly Mock<IProjectSettingsService> _settingsService = new();
    private readonly Mock<IHookInvocationRepository> _hookInvocationRepository = new();
    private readonly Guid _projectId = Guid.NewGuid();

    private HookInvocationRetentionScheduler CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_projectRepository.Object);
        services.AddSingleton(_settingsService.Object);
        services.AddSingleton(_hookInvocationRepository.Object);
        var provider = services.BuildServiceProvider();
        return new HookInvocationRetentionScheduler(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private ProjectSettingsDto SettingsWithRetention(int days) => new(
        EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null,
        OllamaChatModel: null, ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null,
        HookInvocationRetentionDays: days);

    [Fact]
    public async Task PollOnceAsync_deletes_hook_invocations_older_than_the_configured_retention_window()
    {
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(SettingsWithRetention(30));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _hookInvocationRepository.Verify(
            r => r.DeleteOlderThanAsync(
                _projectId,
                It.Is<DateTimeOffset>(cutoff => cutoff <= DateTimeOffset.UtcNow.AddDays(-30) && cutoff > DateTimeOffset.UtcNow.AddDays(-30).AddMinutes(-1)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PollOnceAsync_skips_cleanup_entirely_when_the_project_has_disabled_it()
    {
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(SettingsWithRetention(0));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _hookInvocationRepository.Verify(r => r.DeleteOlderThanAsync(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
