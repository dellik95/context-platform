using Context.Application;
using Context.Application.Abstractions;
using Context.Application.Indexing;
using Context.Domain.Entities;
using Context.Host.BackgroundServices;
using Context.Host.Security;
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
        var group = app.MapGroup("/api/projects").RequireBearerToken();

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetStatusAsync);
        group.MapPost("/{id:guid}/index", TriggerIndex);
        group.MapPost("/{id:guid}/index/cancel", CancelIndex);
        group.MapDelete("/{id:guid}", DeleteProjectAsync);
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

    private static async Task<IResult> GetStatusAsync(Guid id, IServiceProvider services, CancellationToken ct)
    {
        var status = await BuildStatusDtoAsync(id, services, ct).ConfigureAwait(false);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    /// <summary>
    /// Triggers <see cref="IIndexingPipeline.ReindexProjectAsync"/> in the background: a full
    /// project reindex can take a long time, so this endpoint responds 202 immediately rather than
    /// holding the HTTP request open, and runs the actual work in its own DI scope (the request's
    /// own scope is disposed the moment this handler returns). The token it runs with comes from
    /// <see cref="ReindexCancellationRegistry.Begin"/> rather than <see cref="CancellationToken.None"/>,
    /// so a later <c>POST /api/projects/{id}/index/cancel</c> (<see cref="CancelIndex"/>) can actually
    /// stop it.
    /// </summary>
    private static IResult TriggerIndex(
        Guid id,
        bool? full,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        Context.Host.Hubs.IndexingHubNotifier hubNotifier,
        ReindexCancellationRegistry reindexRegistry)
    {
        var fullReindex = full ?? false;
        var logger = loggerFactory.CreateLogger("Context.Host.ReindexProject");
        var ct = reindexRegistry.Begin(id);

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
            try
            {
                await pipeline.ReindexProjectAsync(id, fullReindex, ct).ConfigureAwait(false);
                logger.LogInformation("Reindex of project {ProjectId} (full={Full}) completed.", id, fullReindex);

                var status = await BuildStatusDtoAsync(id, scope.ServiceProvider, CancellationToken.None).ConfigureAwait(false);
                if (status is not null)
                {
                    await hubNotifier.ProjectStatusChangedAsync(id, status, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // A client asked to stop this reindex (CancelIndex below) -- not a failure, so this
                // logs at Info rather than Error. The normal "completed" progress event above never
                // ran (the scan was cut short before reaching it), so the Dashboard's progress UI
                // would otherwise spin forever; send a final Completed:true/Cancelled:true event by
                // hand so it clears.
                logger.LogInformation("Reindex of project {ProjectId} (full={Full}) was cancelled.", id, fullReindex);
                await hubNotifier.ReindexProgressAsync(
                    id, new ReindexProgressDto(id, FilesProcessed: 0, FilesTotal: 0, Completed: true, FilesFailed: 0, Cancelled: true),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background reindex of project {ProjectId} (full={Full}) failed.", id, fullReindex);
            }
            finally
            {
                reindexRegistry.End(id, ct);
            }
        });

        return Results.Accepted(value: new IndexTriggerResponse(id, fullReindex, "started"));
    }

    /// <summary>
    /// Requests cancellation of <paramref name="id"/>'s currently-running reindex (see
    /// <see cref="TriggerIndex"/>). Only stops the foreground scan from walking more files /
    /// enqueuing more background jobs -- jobs already enqueued (EmbedChunk, UpdateGraph, ...) keep
    /// running independently through <c>JobDispatcher</c>; see <see cref="ReindexCancellationRegistry"/>'s
    /// remarks. Idempotent: 404 if nothing is currently running for this project (already finished,
    /// never started, or already cancelled), matching this file's existing "not found" convention
    /// elsewhere rather than treating a no-op cancel as an error.
    /// </summary>
    private static IResult CancelIndex(Guid id, ReindexCancellationRegistry reindexRegistry) =>
        reindexRegistry.Cancel(id) ? Results.Accepted() : Results.NotFound();

    /// <summary>
    /// Permanently removes a project from Context Platform's own database/index: cascade-deletes
    /// every row transitively owned by it (files, symbols, chunks, sessions, memories, graph
    /// nodes/edges, the Claude integration record -- see <see cref="IProjectRepository.DeleteAsync"/>'s
    /// remarks for exactly what cascades and why), plus the two things cascade can't reach
    /// (<see cref="IChunkVectorIndex"/>, a separate sqlite-vec-backed store) and stops anything still
    /// touching the project (an in-progress reindex; the file watcher, if this happens to be the
    /// project this Context.Host process is bound to). The shared, content-hash-keyed
    /// <c>Embeddings</c> cache is deliberately left untouched -- another project may still reference
    /// the same content hash (see <c>EmbedChunkJobHandler</c>'s remarks).
    /// <para/>
    /// Never touches the project's own filesystem (its repo, its
    /// <c>.claude/context-platform.local.json</c>, ...) -- deletion only removes it from Context
    /// Platform's own DB/index, a hard safety boundary.
    /// </summary>
    private static async Task<IResult> DeleteProjectAsync(
        Guid id,
        IProjectRepository projects,
        IChunkVectorIndex vectorIndex,
        ReindexCancellationRegistry reindexRegistry,
        HostProjectContext hostProject,
        FileWatcherHostedService fileWatcher,
        CancellationToken ct)
    {
        var existing = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return Results.NotFound();
        }

        reindexRegistry.Cancel(id);
        if (hostProject.ProjectId == id)
        {
            fileWatcher.StopWatching();
        }

        await vectorIndex.DeleteByProjectAsync(id, ct).ConfigureAwait(false);
        await projects.DeleteAsync(id, ct).ConfigureAwait(false);

        return Results.NoContent();
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
