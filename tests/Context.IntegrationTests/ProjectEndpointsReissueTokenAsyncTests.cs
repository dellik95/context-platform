using Context.Claude.Installation;
using Context.Dashboard.Server.Api;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Shared;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Context.IntegrationTests;

/// <summary>
/// <c>ProjectEndpoints.ReissueTokenAsync</c> rotates the project's token hash, so the project's own
/// installed config must be rewritten with the new raw token -- otherwise every hook in the project
/// keeps sending the old one and 401s.
/// </summary>
public sealed class ProjectEndpointsReissueTokenAsyncTests
{
    private readonly Project _project = new()
    {
        Id = Guid.NewGuid(),
        Name = "widget-service",
        RootPath = "/tmp/widget-service",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ReissueTokenAsync_writes_the_new_token_into_the_projects_installed_config()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync(_project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_project);

        string? installedToken = null;
        var installer = new Mock<IClaudeIntegrationInstaller>(MockBehavior.Strict);
        installer
            .Setup(i => i.InstallMcpOnlyAsync(_project.Id, _project.RootPath, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Callback<Guid, string, CancellationToken, string?>((_, _, _, token) => installedToken = token)
            .Returns(Task.CompletedTask);

        var result = await ProjectEndpoints.ReissueTokenAsync(
            _project.Id, projects.Object, installer.Object, NullLoggerFactory.Instance, CancellationToken.None);

        var returned = result.Should().BeOfType<Ok<ProjectDto>>().Subject.Value!.Token;
        returned.Should().NotBeNullOrWhiteSpace();
        installedToken.Should().Be(returned);
        _project.BearerTokenHash.Should().Be(Hashing.HashToken(returned!));
    }

    [Fact]
    public async Task ReissueTokenAsync_still_returns_the_new_token_when_the_config_cannot_be_written()
    {
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync(_project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_project);

        var installer = new Mock<IClaudeIntegrationInstaller>();
        installer
            .Setup(i => i.InstallMcpOnlyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ThrowsAsync(new UnauthorizedAccessException("read-only root"));

        var result = await ProjectEndpoints.ReissueTokenAsync(
            _project.Id, projects.Object, installer.Object, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<Ok<ProjectDto>>().Subject.Value!.Token.Should().NotBeNullOrWhiteSpace();
        projects.Verify(p => p.UpdateAsync(_project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReissueTokenAsync_returns_404_and_touches_nothing_for_an_unknown_project()
    {
        var projects = new Mock<IProjectRepository>();
        var installer = new Mock<IClaudeIntegrationInstaller>(MockBehavior.Strict);

        var result = await ProjectEndpoints.ReissueTokenAsync(
            Guid.NewGuid(), projects.Object, installer.Object, NullLoggerFactory.Instance, CancellationToken.None);

        result.Should().BeOfType<NotFound>();
    }
}
