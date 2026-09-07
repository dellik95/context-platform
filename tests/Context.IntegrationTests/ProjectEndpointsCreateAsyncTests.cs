using Context.Claude.Installation;
using Context.Dashboard.Server.Api;
using Context.Dashboard.Server.BackgroundServices;
using Context.Domain.Entities;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Context.IntegrationTests;

/// <summary>
/// Covers <c>ProjectEndpoints.CreateAsync</c>'s new automatic MCP-only registration (see
/// <see cref="IClaudeIntegrationInstaller.InstallMcpOnlyAsync"/>'s remarks): a freshly-created
/// project's <c>.mcp.json</c> must be registered immediately, without requiring the separate manual
/// "Install Claude integration" action, and a filesystem failure doing so must never turn project
/// creation itself into a failed request. Invokes the handler directly with mocked/lightweight
/// dependencies (the same <c>internal static</c> direct-invocation pattern
/// <see cref="Context.Dashboard.Server.Hooks.HookEndpoints.PostToolUseAsync"/> already uses) rather
/// than through real HTTP/DI, so no real database or filesystem write is needed.
/// </summary>
public sealed class ProjectEndpointsCreateAsyncTests
{
    /// <summary>
    /// A real (not mocked) <see cref="ProjectFileWatcherManager"/> is cheap and safe to construct
    /// here: <see cref="ProjectFileWatcherManager.StartWatching"/> checks <c>Directory.Exists</c>
    /// first and no-ops with just a warning log when the root doesn't exist on disk — which is
    /// exactly the case for every project id/root used below, so its own real
    /// <c>IServiceScopeFactory</c>/ignore-matcher dependencies are never actually invoked. The class
    /// is <c>sealed</c> with no interface, so it isn't mockable with Moq anyway.
    /// </summary>
    private static ProjectFileWatcherManager CreateFileWatcherManager() =>
        new(scopeFactory: null!, ignoreMatcherFactory: _ => null!, NullLoggerFactory.Instance);

    [Fact]
    public async Task CreateAsync_RegistersMcpOnlyWithTheSameRawTokenReturnedToTheCaller()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByRootPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project?)null);
        projects.Setup(p => p.AddAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project p, CancellationToken _) => p);

        string? capturedToken = null;
        Guid capturedProjectId = default;
        string? capturedRootPath = null;

        var installer = new Mock<IClaudeIntegrationInstaller>(MockBehavior.Strict);
        installer
            .Setup(i => i.InstallMcpOnlyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Callback<Guid, string, CancellationToken, string?>((id, root, _, token) =>
            {
                capturedProjectId = id;
                capturedRootPath = root;
                capturedToken = token;
            })
            .Returns(Task.CompletedTask);

        var rootPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        var request = new CreateProjectRequest("widget-service", rootPath);

        var result = await ProjectEndpoints.CreateAsync(
            request, projects.Object, CreateFileWatcherManager(), installer.Object, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().NotBeNull();

        installer.Verify(
            i => i.InstallMcpOnlyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Once);

        capturedToken.Should().NotBeNullOrWhiteSpace("InstallMcpOnlyAsync must be handed the same raw token CreateAsync already minted for BearerTokenHash, not a second independently generated one");
        capturedProjectId.Should().NotBe(Guid.Empty);
        capturedRootPath.Should().Be(Path.GetFullPath(rootPath));
    }

    /// <summary>
    /// A filesystem failure inside <see cref="IClaudeIntegrationInstaller.InstallMcpOnlyAsync"/> (an
    /// unwritable root path, say) must be logged and swallowed, not propagated -- the project's DB
    /// row already exists by that point and must not be undone by a best-effort side effect failing.
    /// </summary>
    [Fact]
    public async Task CreateAsync_StillSucceedsWhenInstallMcpOnlyAsyncThrows()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByRootPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project?)null);
        projects.Setup(p => p.AddAsync(It.IsAny<Project>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Project p, CancellationToken _) => p);

        var installer = new Mock<IClaudeIntegrationInstaller>(MockBehavior.Strict);
        installer
            .Setup(i => i.InstallMcpOnlyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new UnauthorizedAccessException("root path not writable"));

        var rootPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));
        var request = new CreateProjectRequest("widget-service-failure", rootPath);

        var result = await ProjectEndpoints.CreateAsync(
            request, projects.Object, CreateFileWatcherManager(), installer.Object, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Created<ProjectDto>>(
            "a failure registering MCP must not turn a successful project creation into an error response");
    }
}
