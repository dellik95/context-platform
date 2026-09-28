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
using Moq;

namespace Context.IntegrationTests;

/// <summary>
/// Hooks contain no memory business logic: <see cref="HookEndpoints"/> never does LLM extraction or
/// consolidation itself — it only forwards to <see cref="IContextEngine"/>. Verified here for
/// <c>PostToolUseAsync</c> specifically with a <see cref="MockBehavior.Strict"/>
/// <see cref="IContextEngine"/> mock: only <c>RecordEventAsync</c> is configured, so any other
/// interface member being called (e.g. reaching into search/extraction/consolidation) would throw
/// from the mock rather than silently pass.
/// </summary>
public class HookEndpointsPostToolUseTests
{
    [Fact]
    public async Task PostToolUseAsync_OnlySideEffectIsRecordEventAsync_NoOtherIContextEngineMemberIsCalled()
    {
        var projectId = Guid.NewGuid();
        var claudeSessionId = $"claude-session-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid();

        // Pre-populated so GetOrStartSessionAsync's cache hit skips StartSessionAsync entirely -
        // otherwise this test would be asserting "at most two engine calls," not "exactly one."
        var sessionMap = new ClaudeSessionMap();
        sessionMap.Set(claudeSessionId, projectId, sessionId);

        var engine = new Mock<IContextEngine>(MockBehavior.Strict);
        engine
            .Setup(e => e.RecordEventAsync(projectId, sessionId, SessionEventType.FileRead, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var project = new Project { Id = projectId, Name = "Hook Project", RootPath = "C:/hook-project", CreatedAt = DateTimeOffset.UtcNow };
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CurrentProjectAccessor.HttpContextItemKey] = project;
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);
        var currentProject = new CurrentProjectAccessor(httpContextAccessor.Object);

        var input = new PostToolUseInput(claudeSessionId, "Read", "{}", "{}");

        // Loose (not Strict) - hook-invocation tracking is orthogonal to this test's "no other
        // IContextEngine member is called" assertion, so it just needs to not throw.
        var hookInvocations = new Mock<IHookInvocationRepository>();

        var result = await HookEndpoints.PostToolUseAsync(
            input, engine.Object, currentProject, sessionMap,
            hookInvocations.Object, NullHookInvocationNotifier.Instance, CancellationToken.None);

        result.Should().NotBeNull();
        engine.Verify(
            e => e.RecordEventAsync(projectId, sessionId, SessionEventType.FileRead, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PostToolUseAsync_RecordsRepoRelativePathLiftedOutOfToolInput()
    {
        var projectId = Guid.NewGuid();
        var claudeSessionId = $"claude-session-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid();
        var root = OperatingSystem.IsWindows() ? @"C:\hook-project" : "/hook-project";

        var sessionMap = new ClaudeSessionMap();
        sessionMap.Set(claudeSessionId, projectId, sessionId);

        object? recordedPayload = null;
        var engine = new Mock<IContextEngine>(MockBehavior.Strict);
        engine
            .Setup(e => e.RecordEventAsync(projectId, sessionId, SessionEventType.FileEdit, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, SessionEventType, object, CancellationToken>((_, _, _, payload, _) => recordedPayload = payload)
            .Returns(Task.CompletedTask);

        var project = new Project { Id = projectId, Name = "Hook Project", RootPath = root, CreatedAt = DateTimeOffset.UtcNow };
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CurrentProjectAccessor.HttpContextItemKey] = project;
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);
        var currentProject = new CurrentProjectAccessor(httpContextAccessor.Object);

        var editedFile = Path.Combine(root, "src", "Context.Memory", "X.cs");
        var toolInput = System.Text.Json.JsonSerializer.Serialize(new { file_path = editedFile });
        var input = new PostToolUseInput(claudeSessionId, "Edit", toolInput, "{}");

        await HookEndpoints.PostToolUseAsync(
            input, engine.Object, currentProject, sessionMap,
            new Mock<IHookInvocationRepository>().Object, NullHookInvocationNotifier.Instance, CancellationToken.None);

        recordedPayload.Should().NotBeNull();
        using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(recordedPayload));
        payload.RootElement.GetProperty("path").GetString().Should().Be("src/Context.Memory/X.cs");
    }
}
