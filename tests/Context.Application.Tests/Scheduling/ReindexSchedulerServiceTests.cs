using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Application.Scheduling;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Context.Application.Tests.Scheduling;

public class ReindexSchedulerServiceTests
{
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IProjectSettingsService> _settings = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<IJobRepository> _jobs = new();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly List<string> _calls = [];

    public ReindexSchedulerServiceTests()
    {
        _projects.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _jobs.Setup(j => j.ListActiveByTypeAsync(JobType.ReindexProject, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<ActiveJob>)[]);
        _queue.Setup(q => q.EnqueueAsync(JobType.ReindexProject, It.IsAny<object>(), _projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("enqueue")).ReturnsAsync(Guid.NewGuid());
        _settings.Setup(s => s.UpdateAsync(_projectId, It.IsAny<ProjectSettingsDto>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("stamp")).Returns(Task.CompletedTask);
    }

    private ReindexSchedulerService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_projects.Object);
        services.AddSingleton(_settings.Object);
        services.AddSingleton(_queue.Object);
        services.AddSingleton(_jobs.Object);
        services.AddScoped<ReindexRequester>();
        return new ReindexSchedulerService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private void Settings(bool enabled, int intervalMinutes, DateTimeOffset? last) =>
        _settings.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new ProjectSettingsDto(
            EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null, OllamaChatModel: null,
            ReindexEnabled: enabled, ReindexIntervalMinutes: intervalMinutes, LastReindexScheduledAt: last));

    [Fact]
    public async Task PollOnceAsync_queues_a_due_reindex_and_stamps_it_only_afterwards()
    {
        Settings(enabled: true, 60, DateTimeOffset.UtcNow.AddHours(-2));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        Assert.Equal(["enqueue", "stamp"], _calls);
    }

    [Fact]
    public async Task PollOnceAsync_does_not_stamp_when_queueing_fails_so_the_next_poll_retries()
    {
        Settings(enabled: true, 60, null);
        _queue.Setup(q => q.EnqueueAsync(JobType.ReindexProject, It.IsAny<object>(), _projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _settings.Verify(s => s.UpdateAsync(It.IsAny<Guid>(), It.IsAny<ProjectSettingsDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnceAsync_skips_the_job_but_still_stamps_when_a_reindex_is_already_active()
    {
        Settings(enabled: true, 60, DateTimeOffset.UtcNow.AddHours(-2));
        _jobs.Setup(j => j.ListActiveByTypeAsync(JobType.ReindexProject, _projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ActiveJob(Guid.NewGuid(), DateTimeOffset.UtcNow)]);

        await CreateSut().PollOnceAsync(CancellationToken.None);

        Assert.Equal(["stamp"], _calls);
    }

    [Theory]
    [InlineData(false, 60, 120)]
    [InlineData(true, 60, 10)]
    public async Task PollOnceAsync_does_nothing_when_disabled_or_not_yet_due(bool enabled, int intervalMinutes, int minutesSinceLast)
    {
        Settings(enabled, intervalMinutes, DateTimeOffset.UtcNow.AddMinutes(-minutesSinceLast));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        Assert.Empty(_calls);
    }
}
