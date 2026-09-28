using Context.Application;
using Context.Application.Notifications;
using Context.Claude.Hooks;
using Context.Dashboard.Server.Hooks;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Context.IntegrationTests;

public class HookEndpointsStopAndCompactTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly string _claudeSessionId = $"claude-session-{Guid.NewGuid():N}";
    private readonly ClaudeSessionMap _sessionMap = new();
    private readonly Mock<IContextEngine> _engine = new(MockBehavior.Strict);
    private readonly CurrentProjectAccessor _currentProject;

    public HookEndpointsStopAndCompactTests()
    {
        _sessionMap.Set(_claudeSessionId, _projectId, _sessionId);

        var project = new Project { Id = _projectId, Name = "Hook Project", RootPath = "C:/hook-project", CreatedAt = DateTimeOffset.UtcNow };
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CurrentProjectAccessor.HttpContextItemKey] = project;
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);
        _currentProject = new CurrentProjectAccessor(httpContextAccessor.Object);
    }

    [Fact]
    public async Task StopAsync_RecordsTheAssistantReplyAndNeverReturnsContext()
    {
        object? recordedPayload = null;
        _engine
            .Setup(e => e.RecordEventAsync(_projectId, _sessionId, SessionEventType.AssistantMessage, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, SessionEventType, object, CancellationToken>((_, _, _, payload, _) => recordedPayload = payload)
            .Returns(Task.CompletedTask);

        var result = await HookEndpoints.StopAsync(
            new StopInput(_claudeSessionId, "Switched the retry policy to Polly."), _engine.Object, _currentProject, _sessionMap,
            new Mock<IHookInvocationRepository>().Object, NullHookInvocationNotifier.Instance, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull();
        using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(recordedPayload));
        payload.RootElement.GetProperty("message").GetString().Should().Be("Switched the retry policy to Polly.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task StopAsync_SkipsAnEmptyReply(string? message)
    {
        var result = await HookEndpoints.StopAsync(
            new StopInput(_claudeSessionId, message), _engine.Object, _currentProject, _sessionMap,
            new Mock<IHookInvocationRepository>().Object, NullHookInvocationNotifier.Instance, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull();
        _engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PostCompactAsync_ExtractsFromTheStdinSummaryWithoutATranscript()
    {
        _engine
            .Setup(e => e.ExtractMemoryFromCompactSummaryAsync(_projectId, _sessionId, "Chose Polly over hand-rolled retries.", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _engine
            .Setup(e => e.RequestSessionSummaryAsync(_projectId, _sessionId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await HookEndpoints.PostCompactAsync(
            new PostCompactInput(_claudeSessionId, "auto", CompactSummary: "Chose Polly over hand-rolled retries."),
            _engine.Object, _currentProject, _sessionMap,
            new Mock<IHookInvocationRepository>().Object, NullHookInvocationNotifier.Instance, CancellationToken.None);

        _engine.Verify(
            e => e.ExtractMemoryFromCompactSummaryAsync(_projectId, _sessionId, "Chose Polly over hand-rolled retries.", It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
