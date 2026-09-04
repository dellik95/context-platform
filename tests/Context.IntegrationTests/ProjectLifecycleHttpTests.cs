using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Context.Dashboard.Server.Api;
using FluentAssertions;

namespace Context.IntegrationTests;

/// <summary>
/// Real-HTTP coverage for the two project-lifecycle endpoints added alongside "stop an
/// in-progress reindex" and "remove a project entirely": <c>POST /api/projects/{id}/index/cancel</c>
/// and <c>DELETE /api/projects/{id}</c>. Shares <see cref="HostHttpE2ETests"/>'s
/// <see cref="HostHttpE2ETests.HostHttpWebApplicationFactory"/> and xunit collection for the same
/// reason <see cref="HostHttpProductionCorsTests"/> does -- only one real <c>Program</c> instance
/// may be live at a time (see that factory's own remarks).
/// </summary>
[Collection(HostHttpE2ETests.CollectionName)]
public sealed class ProjectLifecycleHttpTests : IClassFixture<HostHttpE2ETests.HostHttpWebApplicationFactory>
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HostHttpE2ETests.HostHttpWebApplicationFactory _factory;

    public ProjectLifecycleHttpTests(HostHttpE2ETests.HostHttpWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The registry-level "actually stops a running scan" behavior is covered deterministically by
    /// <c>ReindexCancellationRegistryTests</c> and <c>IndexingPipelineTests</c> (a real HTTP
    /// round-trip racing a background <c>Task.Run</c> reindex against a second HTTP call would be
    /// flaky on a slow CI box). This instead covers the endpoint's own idempotent-no-op contract:
    /// cancelling a project with nothing running is a clean 404, not a 500 or a false-positive 202.
    /// </summary>
    [Fact]
    public async Task CancelIndex_returns_404_when_no_reindex_is_running_for_the_project()
    {
        using var client = _factory.CreateClient();
        var project = await CreateProjectAsync(client, "cancel-idle");

        // The project's own freshly-minted token authorizes this (RequireOwnProjectOrAdminAsync),
        // same as a real Dashboard tab acting on the project it's scoped to.
        using var request = AuthorizedRequest(HttpMethod.Post, $"/api/projects/{project.Id}/index/cancel", project.Token!);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CancelIndex_returns_404_for_a_project_id_that_does_not_exist()
    {
        using var client = _factory.CreateClient();

        // No project exists with this id, so no bearer token could ever resolve to it -- only the
        // admin token can pass RequireOwnProjectOrAdminAsync's filter here.
        using var request = AuthorizedRequest(HttpMethod.Post, $"/api/projects/{Guid.NewGuid()}/index/cancel", _factory.AdminToken);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProject_removes_it_so_a_subsequent_GET_404s_and_a_second_delete_also_404s()
    {
        using var client = _factory.CreateClient();
        var project = await CreateProjectAsync(client, "delete-me");

        using var deleteRequest = AuthorizedRequest(HttpMethod.Delete, $"/api/projects/{project.Id}", project.Token!);
        var deleteResponse = await client.SendAsync(deleteRequest);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The project's own token no longer resolves to anything once its row (and BearerTokenHash)
        // is gone -- these follow-up calls would 401 at the auth filter with it, not reach the
        // handler's 404. The admin token is what proves the *domain* 404, not an auth failure.
        using var getRequest = AuthorizedRequest(HttpMethod.Get, $"/api/projects/{project.Id}", _factory.AdminToken);
        var getResponse = await client.SendAsync(getRequest);
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "the project's own DB row and its cascaded data are gone");

        using var secondDeleteRequest = AuthorizedRequest(HttpMethod.Delete, $"/api/projects/{project.Id}", _factory.AdminToken);
        var secondDeleteResponse = await client.SendAsync(secondDeleteRequest);
        secondDeleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "deleting an already-deleted project must not silently succeed or 500");
    }

    [Fact]
    public async Task DeleteProject_returns_404_for_a_project_id_that_never_existed()
    {
        using var client = _factory.CreateClient();

        using var request = AuthorizedRequest(HttpMethod.Delete, $"/api/projects/{Guid.NewGuid()}", _factory.AdminToken);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteProject_requires_a_bearer_token()
    {
        using var client = _factory.CreateClient();
        var project = await CreateProjectAsync(client, "delete-unauthorized");

        var response = await client.DeleteAsync($"/api/projects/{project.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no Authorization header was sent");
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string bearerToken, object? jsonBody = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
        {
            request.Content = JsonContent.Create(jsonBody);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return request;
    }

    /// <summary>Project creation is admin-token-gated (a brand-new project has no bearer token of its
    /// own yet) -- see <c>ProjectEndpoints.CreateAsync</c>'s remarks.</summary>
    private async Task<ProjectDto> CreateProjectAsync(HttpClient client, string name)
    {
        var rootPath = Path.Combine(_factory.TempRoot, name);
        Directory.CreateDirectory(rootPath);

        using var request = AuthorizedRequest(HttpMethod.Post, "/api/projects", _factory.AdminToken, new CreateProjectRequest(name, rootPath));
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<ProjectDto>(ResponseJsonOptions);
        created.Should().NotBeNull();
        return created!;
    }
}
