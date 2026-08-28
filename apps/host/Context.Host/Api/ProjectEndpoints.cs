using Context.Application;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;

namespace Context.Host.Api;

/// <summary>
/// <c>/api/projects</c> — project CRUD-lite plus the reindex trigger, per the Context.Host task
/// brief §4. Every handler is a thin wrapper over <see cref="IProjectRepository"/> (for the parts
/// CONTRACTS.md's <see cref="IContextEngine"/> facade doesn't cover — plain project listing/creation
/// isn't part of its fixed shape) and <see cref="IContextEngine"/> itself for project status.
/// </summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetStatusAsync);
        group.MapPost("/{id:guid}/index", TriggerIndex);
    }

    private static async Task<IResult> CreateAsync(CreateProjectRequest request, IProjectRepository projects, CancellationToken ct)
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

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            RootPath = rootPath,
            VcsRemote = request.VcsRemote,
            CreatedAt = DateTimeOffset.UtcNow
        };
        project = await projects.AddAsync(project, ct).ConfigureAwait(false);

        return Results.Created($"/api/projects/{project.Id}", ProjectDto.FromEntity(project));
    }

    private static async Task<IResult> ListAsync(IProjectRepository projects, CancellationToken ct)
    {
        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        return Results.Ok(all.Select(ProjectDto.FromEntity));
    }

    private static async Task<IResult> GetStatusAsync(Guid id, IProjectRepository projects, IContextEngine engine, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (project is null)
        {
            return Results.NotFound();
        }

        var dto = ProjectDto.FromEntity(project);
        var infoObj = await engine.ProjectInfoAsync(id, ct).ConfigureAwait(false);

        return infoObj is ProjectInfo info
            ? Results.Ok(new ProjectStatusDto(dto, info.FilesIndexed, info.SymbolsIndexed, info.MemoriesCount, info.LastIndexedAt))
            : Results.Ok(new ProjectStatusDto(dto, FilesIndexed: 0, SymbolsIndexed: 0, MemoriesCount: 0, LastIndexedAt: null));
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
        ILoggerFactory loggerFactory)
    {
        var fullReindex = full ?? false;
        var logger = loggerFactory.CreateLogger("Context.Host.ReindexProject");

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
            try
            {
                await pipeline.ReindexProjectAsync(id, fullReindex, CancellationToken.None).ConfigureAwait(false);
                logger.LogInformation("Reindex of project {ProjectId} (full={Full}) completed.", id, fullReindex);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background reindex of project {ProjectId} (full={Full}) failed.", id, fullReindex);
            }
        });

        return Results.Accepted(value: new IndexTriggerResponse(id, fullReindex, "started"));
    }
}
