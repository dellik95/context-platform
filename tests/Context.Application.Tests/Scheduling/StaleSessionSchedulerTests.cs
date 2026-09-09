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

public class StaleSessionSchedulerTests
{
    private readonly Mock<IProjectRepository> _projectRepository = new();
    private readonly Mock<IProjectSettingsService> _settingsService = new();
    private readonly Mock<ISessionRepository> _sessionRepository = new();
    private readonly Mock<IContextEngine> _engine = new();
    private readonly Guid _projectId = Guid.NewGuid();

    private StaleSessionScheduler CreateSut()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_projectRepository.Object);
        services.AddSingleton(_settingsService.Object);
        services.AddSingleton(_sessionRepository.Object);
        services.AddSingleton(_engine.Object);
        var provider = services.BuildServiceProvider();
        return new StaleSessionScheduler(provider.GetRequiredService<IServiceScopeFactory>());
    }

    private ProjectSettingsDto SettingsWithTimeout(int minutes) => new(
        EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null,
        OllamaChatModel: null, ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null,
        StaleSessionTimeoutMinutes: minutes);

    [Fact]
    public async Task PollOnceAsync_ends_an_active_session_whose_last_event_is_older_than_the_timeout()
    {
        var session = new Session { Id = Guid.NewGuid(), ProjectId = _projectId, Status = SessionStatus.Active, StartedAt = DateTimeOffset.UtcNow.AddHours(-2) };
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(SettingsWithTimeout(60));
        _sessionRepository.Setup(r => r.ListByProjectAsync(_projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SessionListItem(session, Title: null)]);
        _sessionRepository.Setup(r => r.GetEventsAsync(_projectId, session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SessionEvent { Id = Guid.NewGuid(), SessionId = session.Id, Type = SessionEventType.ToolCall, PayloadJson = "{}", Timestamp = DateTimeOffset.UtcNow.AddHours(-2) }]);

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _engine.Verify(e => e.EndSessionAsync(_projectId, session.Id, It.IsAny<CancellationToken>(), "stale-timeout"), Times.Once);
    }

    [Fact]
    public async Task PollOnceAsync_leaves_an_active_session_alone_when_its_last_event_is_within_the_timeout()
    {
        var session = new Session { Id = Guid.NewGuid(), ProjectId = _projectId, Status = SessionStatus.Active, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(SettingsWithTimeout(60));
        _sessionRepository.Setup(r => r.ListByProjectAsync(_projectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SessionListItem(session, Title: null)]);
        _sessionRepository.Setup(r => r.GetEventsAsync(_projectId, session.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SessionEvent { Id = Guid.NewGuid(), SessionId = session.Id, Type = SessionEventType.ToolCall, PayloadJson = "{}", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5) }]);

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _engine.Verify(e => e.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PollOnceAsync_skips_cleanup_entirely_when_the_project_has_disabled_it()
    {
        var session = new Session { Id = Guid.NewGuid(), ProjectId = _projectId, Status = SessionStatus.Active, StartedAt = DateTimeOffset.UtcNow.AddDays(-1) };
        _projectRepository.Setup(r => r.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Project { Id = _projectId, Name = "p", RootPath = "/repo" }]);
        _settingsService.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(SettingsWithTimeout(0));

        await CreateSut().PollOnceAsync(CancellationToken.None);

        _sessionRepository.Verify(r => r.ListByProjectAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _engine.Verify(e => e.EndSessionAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }
}
