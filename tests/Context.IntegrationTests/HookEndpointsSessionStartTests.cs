using Context.Application;
using Context.Dashboard.Server.Hooks;
using Context.Domain.Enums;
using FluentAssertions;
using Moq;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.IntegrationTests;

public sealed class HookEndpointsSessionStartTests
{
    [Fact]
    public async Task TryBuildSessionStartContextAsync_lists_top_memories_with_canonical_headers()
    {
        var projectId = Guid.NewGuid();
        var memories = Enumerable.Range(1, 7)
            .Select(i => new DomainMemory
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Type = MemoryType.Decision,
                Topic = "Auth",
                Title = $"Decision {i}",
                Body = "body",
                Confidence = 0.8,
                SourceCount = i,
            })
            .ToList();

        var engine = new Mock<IContextEngine>(MockBehavior.Strict);
        engine.Setup(e => e.SearchMemoryAsync(projectId, string.Empty, false, It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(memories);
        engine.Setup(e => e.ProjectInfoAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectInfo(projectId, "p", "/repo", 10, 20, 7, null));

        var context = await HookEndpoints.TryBuildSessionStartContextAsync(engine.Object, projectId, CancellationToken.None);

        context.Should().StartWith("Context Platform - project memory:\n10 files / 20 symbols indexed, 7 memories on record.\n");
        context.Should().Contain("- [Decision · Auth · conf 0.80 · 1 source] Decision 1\n");
        context.Should().Contain("- [Decision · Auth · conf 0.80 · 5 sources] Decision 5\n");
        context.Should().NotContain("Decision 6");
    }

    [Fact]
    public async Task TryBuildSessionStartContextAsync_returns_null_when_memory_search_fails()
    {
        var engine = new Mock<IContextEngine>();
        engine.Setup(e => e.SearchMemoryAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var context = await HookEndpoints.TryBuildSessionStartContextAsync(engine.Object, Guid.NewGuid(), CancellationToken.None);

        context.Should().BeNull();
    }
}
