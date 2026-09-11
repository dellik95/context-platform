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
/// MEMORY_PIPELINE_PLAN.md §H case #11 ("hooks contain no memory business logic"), §C row 9: this
/// is the standing architectural rule that <see cref="HookEndpoints"/> never does LLM extraction or
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
            .Setup(e => e.RecordEventAsync(sessionId, SessionEventType.FileRead, It.IsAny<object>(), It.IsAny<CancellationToken>()))
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
            e => e.RecordEventAsync(sessionId, SessionEventType.FileRead, It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
