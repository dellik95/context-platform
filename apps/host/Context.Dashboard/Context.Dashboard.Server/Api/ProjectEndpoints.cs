using Context.Application;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Dashboard.Server.BackgroundServices;
using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;
using Context.Shared;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>/api/projects</c> — project CRUD-lite plus the reindex trigger, per the Context.Dashboard.Server task
/// brief §4. Every handler is a thin wrapper over <see cref="IProjectRepository"/> (for the parts
/// CONTRACTS.md's <see cref="IContextEngine"/> facade doesn't cover — plain project listing/creation
/// isn't part of its fixed shape) and <see cref="IContextEngine"/> itself for project status.
/// <para/>
/// <c>CreateAsync</c> and <c>ListAsync</c> are gated by <see cref="AdminTokenAuth"/> rather than the
/// regular per-project <see cref="BearerTokenAuth"/>: a brand-new project has no bearer token yet
/// (only the admin token can authorize minting its first one), and the project list itself is
/// cross-tenant information no single project's own token should be able to read on a host shared
/// by many projects. <c>GetStatusAsync</c>/<c>TriggerIndex</c> operate on one already-known project
/// and are gated by <see cref="RequireOwnProjectOrAdminAsync"/>: either the caller's bearer token
/// resolves (via <see cref="BearerTokenAuth"/>) to the same project as the <c>{id}</c> route value —
/// stashed for <see cref="CurrentProjectAccessor"/> exactly like <see cref="BearerTokenAuth.RequireBearerTokenFilter"/>
/// does — or the caller presents the admin token, which can act on any project.
/// </summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/", CreateAsync).RequireAdminToken();
        group.MapGet("/", ListAsync).RequireAdminToken();
        group.MapGet("/{id:guid}", GetStatusAsync).AddEndpointFilter(RequireOwnProjectOrAdminAsync);
        group.MapPost("/{id:guid}/index", TriggerIndex).AddEndpointFilter(RequireOwnProjectOrAdminAsync);
        group.MapPost("/{id:guid}/token", ReissueTokenAsync).RequireAdminToken();
    }

    /// <summary>
    /// Endpoint filter for the <c>{id:guid}</c> routes above: authorized when either the admin token
    /// is presented, or the caller's bearer token resolves to the very project named by the route's
    /// <c>id</c> value. A bearer token that resolves to a *different* project is rejected — the
    /// route id is not itself an authorization credential.
    /// </summary>
    private static async ValueTask<object?> RequireOwnProjectOrAdminAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var adminToken = http.RequestServices.GetRequiredService<AdminTokenProvider>();
        if (AdminTokenAuth.IsAuthorized(http, adminToken))
        {
            return await next(ctx).ConfigureAwait(false);
        }

        var projects = http.RequestServices.GetRequiredService<IProjectRepository>();
        var project = await BearerTokenAuth.TryResolveProjectAsync(http, projects, http.RequestAborted).ConfigureAwait(false);
        if (project is null)
        {
            return Results.Unauthorized();
        }

        var requestedId = http.Request.RouteValues["id"] as string;
        if (!Guid.TryParse(requestedId, out var id) || id != project.Id)
        {
            return Results.Problem("This bearer token does not authorize access to the requested project.", statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[CurrentProjectAccessor.HttpContextItemKey] = project;

        return await next(ctx).ConfigureAwait(false);
    }

    private static async Task<IResult> CreateAsync(
        CreateProjectRequest request,
        IProjectRepository projects,
        ProjectFileWatcherManager fileWatcherManager,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RootPath))
        {
            return Results.BadRequest("Both 'name' and 'rootPath' are required.");
        }

        var rootPath = Path.GetFullPath(request.RootPath);
        var existing = await projects.GetByRootPathAsync(rootPath, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return Results.Conflict(ProjectDto.FromEntity(existing));
        }

        // Mint the project's bearer token here rather than leaving it null until a later
        // `context claude install` run: only the raw token's hash is ever persisted
        // (Project.BearerTokenHash), so this response is the one and only moment the raw value is
        // ever available to return to the caller.
        var rawToken = BearerTokenGenerator.Generate();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            RootPath = rootPath,
            VcsRemote = request.VcsRemote,
            CreatedAt = DateTimeOffset.UtcNow,
            BearerTokenHash = Hashing.HashToken(rawToken),
            BearerTokenIssuedAt = DateTimeOffset.UtcNow
        };
        project = await projects.AddAsync(project, ct).ConfigureAwait(false);

        // Starts indexing/watching this project immediately rather than only from the next host
        // restart — ProjectFileWatcherManager.StartAsync only sweeps projects that already existed
        // when the host itself started.
        fileWatcherManager.StartWatching(project);

        return Results.Created($"/api/projects/{project.Id}", ProjectDto.FromEntity(project) with { Token = rawToken });
    }

    private static async Task<IResult> ListAsync(IProjectRepository projects, CancellationToken ct)
    {
        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        return Results.Ok(all.Select(ProjectDto.FromEntity));
    }

    /// <summary>
    /// Admin-only, deliberately not <see cref="RequireOwnProjectOrAdminAsync"/>: this exists
    /// specifically for the case where the caller does NOT have the project's own bearer token any
    /// more (a browser tab that never captured it, or lost sessionStorage — see
    /// ProjectTokenService.ts's remarks) — only the admin token can authorize minting a
    /// replacement. Rotates <see cref="Project.BearerTokenHash"/>, which invalidates the old raw
    /// token everywhere it's in use (other browser tabs, an installed CLI/hook config, MCP) — an
    /// accepted tradeoff for a deliberate, authenticated "I lost access, give me a new one" action.
    /// </summary>
    private static async Task<IResult> ReissueTokenAsync(Guid id, IProjectRepository projects, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (project is null)
        {
            return Results.NotFound();
        }

        var rawToken = BearerTokenGenerator.Generate();
        project.BearerTokenHash = Hashing.HashToken(rawToken);
        project.BearerTokenIssuedAt = DateTimeOffset.UtcNow;
        await projects.UpdateAsync(project, ct).ConfigureAwait(false);

        return Results.Ok(ProjectDto.FromEntity(project) with { Token = rawToken });
    }

    private static async Task<IResult> GetStatusAsync(Guid id, IServiceProvider services, CancellationToken ct)
    {
        var status = await BuildStatusDtoAsync(id, services, ct).ConfigureAwait(false);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    /// <summary>
    /// Triggers <see cref="IIndexingPipeline.ReindexProjectAsync"/> in the background: a full
    /// project reindex can take a long time, so this endpoint responds 202 immediately rather than
    /// holding the HTTP request open, and runs the actual work in its own DI scope (the request's
    /// own scope is disposed the moment this handler returns).
    /// </summary>
    private static IResult TriggerIndex(
        Guid id,
        bool? full,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        Context.Dashboard.Server.Hubs.IndexingHubNotifier hubNotifier)
    {
        var fullReindex = full ?? false;
        var logger = loggerFactory.CreateLogger("Context.Dashboard.Server.ReindexProject");

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
            try
            {
                await pipeline.ReindexProjectAsync(id, fullReindex, CancellationToken.None).ConfigureAwait(false);
                logger.LogInformation("Reindex of project {ProjectId} (full={Full}) completed.", id, fullReindex);

                var status = await BuildStatusDtoAsync(id, scope.ServiceProvider, CancellationToken.None).ConfigureAwait(false);
                if (status is not null)
                {
                    await hubNotifier.ProjectStatusChangedAsync(id, status, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background reindex of project {ProjectId} (full={Full}) failed.", id, fullReindex);
            }
        });

        return Results.Accepted(value: new IndexTriggerResponse(id, fullReindex, "started"));
    }

    private static async Task<ProjectStatusDto?> BuildStatusDtoAsync(Guid id, IServiceProvider services, CancellationToken ct)
    {
        var projects = services.GetRequiredService<IProjectRepository>();
        var engine = services.GetRequiredService<IContextEngine>();

        var project = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var dto = ProjectDto.FromEntity(project);
        var infoObj = await engine.ProjectInfoAsync(id, ct).ConfigureAwait(false);

        return infoObj is ProjectInfo info
            ? new ProjectStatusDto(dto, info.FilesIndexed, info.SymbolsIndexed, info.MemoriesCount, info.LastIndexedAt)
            : new ProjectStatusDto(dto, FilesIndexed: 0, SymbolsIndexed: 0, MemoriesCount: 0, LastIndexedAt: null);
    }
}
