using Context.Dashboard.Server.Hubs;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace Context.IntegrationTests;

public sealed class IndexingHubJoinProjectTests
{
    private readonly Project _project = new()
    {
        Id = Guid.NewGuid(),
        Name = "widget-service",
        RootPath = "/tmp/widget-service",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private readonly Mock<IGroupManager> _groups = new();

    [Fact]
    public async Task JoinProject_adds_the_connection_to_the_group_of_the_project_its_token_resolved_to()
    {
        using var hub = CreateHub(_project);

        await hub.JoinProject(_project.Id);

        _groups.Verify(g => g.AddToGroupAsync("conn-1", IndexingHub.GroupName(_project.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinProject_rejects_a_project_other_than_the_one_the_token_resolved_to()
    {
        using var hub = CreateHub(_project);

        var act = () => hub.JoinProject(Guid.NewGuid());

        await act.Should().ThrowAsync<HubException>();
        _groups.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JoinProject_rejects_a_connection_with_no_resolved_project()
    {
        using var hub = CreateHub(resolvedProject: null);

        var act = () => hub.JoinProject(_project.Id);

        await act.Should().ThrowAsync<HubException>();
        _groups.VerifyNoOtherCalls();
    }

    private IndexingHub CreateHub(Project? resolvedProject)
    {
        var http = new DefaultHttpContext();
        if (resolvedProject is not null)
        {
            http.Items[CurrentProjectAccessor.HttpContextItemKey] = resolvedProject;
        }

        var features = new FeatureCollection();
        features.Set<IHttpContextFeature>(new HttpContextFeature { HttpContext = http });

        var context = new Mock<HubCallerContext>();
        context.SetupGet(c => c.ConnectionId).Returns("conn-1");
        context.SetupGet(c => c.Features).Returns(features);

        return new IndexingHub { Context = context.Object, Groups = _groups.Object };
    }

    private sealed class HttpContextFeature : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }
}
