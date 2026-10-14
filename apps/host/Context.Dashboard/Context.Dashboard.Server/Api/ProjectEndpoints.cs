using Context.Application;
using Context.Application.Abstractions;
using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Claude.Installation;
using Context.Domain.Entities;
using Context.Domain.Enums;
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
/// and are gated by <see cref="ProjectScopeAuth.RequireOwnProjectFilter"/>: either the caller's
/// bearer token resolves to the same project as the <c>{id}</c> route value, or the caller presents
/// the admin token, which can act on any project.
/// </summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects");

        group.MapPost("/", CreateAsync).RequireAdminToken();
        group.MapGet("/", ListAsync).RequireAdminToken();
        group.MapGet("/{id:guid}", GetStatusAsync).RequireOwnProject();
        group.MapPost("/{id:guid}/index", TriggerIndex).RequireOwnProject();
        group.MapPost("/{id:guid}/index/cancel", CancelIndex).RequireOwnProject();
        group.MapPost("/{id:guid}/embeddings/recreate", TriggerRecreateEmbeddings).RequireOwnProject();
        group.MapPost("/{id:guid}/token", ReissueTokenAsync).RequireAdminToken();
        group.MapDelete("/{id:guid}", DeleteProjectAsync).RequireOwnProject();
    }

    /// <summary>
    /// Exposed as <c>internal</c> (see <c>InternalsVisibleTo</c> in Context.Dashboard.Server.csproj),
    /// the same pattern <see cref="Context.Dashboard.Server.Hooks.HookEndpoints.PostToolUseAsync"/>
    /// already uses, so Context.IntegrationTests can invoke this Minimal API handler directly with a
    /// mock <see cref="IClaudeIntegrationInstaller"/> and assert the auto MCP-registration call and
    /// its failure handling — without going through real HTTP routing/DI just to reach a plain
    /// static method.
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        CreateProjectRequest request,
        IProjectRepository projects,
        ProjectFileWatcherManager fileWatcherManager,
        IClaudeIntegrationInstaller claudeInstaller,
        ILoggerFactory loggerFactory,
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

        // Registers this project's MCP server (.mcp.json + context-platform.local.json) right away
        // so its MCP tools work the moment the project exists, without the separate manual "Install
        // Claude integration" step -- that full six-artifact install (hooks, settings.json,
        // plugin.json, skill doc, .gitignore) stays exactly as-is, still reachable via the explicit
        // POST /{id}/claude/install action. Reuses `rawToken` above rather than minting a second one:
        // the .mcp.json entry and Project.BearerTokenHash must reference the same raw token.
        // Best-effort -- a filesystem failure here (e.g. an unwritable root path) must not undo the
        // DB row that already exists, so it's logged and swallowed rather than propagated.
        try
        {
            await claudeInstaller.InstallMcpOnlyAsync(project.Id, project.RootPath, ct, bearerToken: rawToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Context.Dashboard.Server.CreateProject").LogWarning(
                ex,
                "Failed to auto-register the MCP integration for project {ProjectId} at {RootPath}; the project was still created and can be installed manually later.",
                project.Id, project.RootPath);
        }

        // Starts indexing/watching this project immediately rather than only from the next host
        // restart — ProjectFileWatcherManager.StartAsync only sweeps projects that already existed
        // when the host itself started. In the background and after the MCP install: watching a
        // large /mnt/c root took over half an hour, holding the request open and leaving the
        // project's hooks on a stale token the whole time.
        fileWatcherManager.StartWatchingInBackground(project);

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
    /// more (a browser tab that never captured it, or lost localStorage — see
    /// ProjectTokenService.ts's remarks) — only the admin token can authorize minting a
    /// replacement. Rotates <see cref="Project.BearerTokenHash"/>, which invalidates the old raw
    /// token everywhere it's in use (other browser tabs, a running MCP connection) — an accepted
    /// tradeoff for a deliberate, authenticated "I lost access, give me a new one" action. The
    /// project's own <c>.mcp.json</c> and <c>context-platform.local.json</c> are rewritten with the
    /// new token: the Dashboard reissues silently whenever a browser has no stored token, and
    /// leaving those files on the old one made every hook in the project 401 from then on.
    /// </summary>
    internal static async Task<IResult> ReissueTokenAsync(
        Guid id,
        IProjectRepository projects,
        IClaudeIntegrationInstaller claudeInstaller,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
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

        try
        {
            await claudeInstaller.InstallMcpOnlyAsync(project.Id, project.RootPath, ct, bearerToken: rawToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("Context.Dashboard.Server.ReissueToken").LogWarning(
                ex,
                "Reissued the token for project {ProjectId} but could not write it to {RootPath}; its hooks and MCP keep the old token until the Claude integration is reinstalled.",
                project.Id, project.RootPath);
        }

        return Results.Ok(ProjectDto.FromEntity(project) with { Token = rawToken });
    }

    private static async Task<IResult> GetStatusAsync(Guid id, IServiceProvider services, CancellationToken ct)
    {
        var status = await BuildStatusDtoAsync(id, services, ct).ConfigureAwait(false);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    /// <summary>
    /// Queues a <see cref="JobType.ReindexProject"/> job: a full project reindex can take a long time, so
    /// this endpoint responds 202 as soon as the job is stored rather than holding the HTTP request open, and
    /// the job survives a restart. Once running, it is registered with <see cref="ReindexCancellationRegistry"/>,
    /// so a later <c>POST /api/projects/{id}/index/cancel</c> (<see cref="CancelIndex"/>) can actually stop it,
    /// and a newer request replaces it.
    /// </summary>
    private static async Task<IResult> TriggerIndex(Guid id, bool? full, ReindexRequester requester, CancellationToken ct)
    {
        var fullReindex = full ?? false;
        await requester.RequestAsync(id, fullReindex, skipIfActive: false, ct).ConfigureAwait(false);
        return Results.Accepted(value: new IndexTriggerResponse(id, fullReindex, "queued"));
    }

    /// <summary>
    /// Requests cancellation of <paramref name="id"/>'s currently-running reindex (see
    /// <see cref="TriggerIndex"/>). Only stops the foreground scan from walking more files /
    /// enqueuing more background jobs -- jobs already enqueued (EmbedChunk, UpdateGraph, ...) keep
    /// running independently through <c>JobDispatcher</c>; see <see cref="ReindexCancellationRegistry"/>'s
    /// remarks. A reindex still waiting in the job queue is removed too, with a final Cancelled progress event so
    /// the dashboard's card clears. Idempotent: 404 if nothing is queued or running for this project (already
    /// finished, never started, or already cancelled), matching this file's existing "not found" convention
    /// elsewhere rather than treating a no-op cancel as an error.
    /// </summary>
    private static async Task<IResult> CancelIndex(
        Guid id, ReindexCancellationRegistry reindexRegistry, IJobRepository jobs, IIndexingProgressReporter progressReporter, CancellationToken ct)
    {
        var running = reindexRegistry.Cancel(id);
        var queued = await jobs.DeleteByStatusAsync(JobStatus.Pending, id, JobType.ReindexProject, null, ct).ConfigureAwait(false);
        if (queued > 0)
        {
            await progressReporter.ReportAsync(new ReindexProgressDto(id, 0, 0, Completed: true, Cancelled: true), ct).ConfigureAwait(false);
        }

        return running || queued > 0 ? Results.Accepted() : Results.NotFound();
    }

    /// <summary>
    /// Enqueues a <see cref="JobType.RecreateEmbeddings"/> job for <paramref name="id"/> and returns
    /// immediately -- unlike <see cref="TriggerIndex"/>, there is no <c>Task.Run</c>/pipeline call
    /// here: <see cref="JobDispatcher"/>'s own background worker picks the job up and does the
    /// actual work, so this handler's only job is to enqueue it.
    /// </summary>
    private static async Task<IResult> TriggerRecreateEmbeddings(Guid id, IJobQueue jobQueue, CancellationToken ct)
    {
        var jobId = await jobQueue.EnqueueAsync(JobType.RecreateEmbeddings, new RecreateEmbeddingsPayload(id), projectId: id, ct: ct).ConfigureAwait(false);
        return Results.Accepted(value: new RecreateEmbeddingsTriggerResponse(id, jobId, "queued"));
    }

    /// <summary>
    /// Permanently removes a project from Context Platform's own database/index: cascade-deletes
    /// every row transitively owned by it (files, symbols, chunks, sessions, memories, graph
    /// nodes/edges, the Claude integration record -- see <see cref="IProjectRepository.DeleteAsync"/>'s
    /// remarks for exactly what cascades and why), plus the two things cascade can't reach
    /// (<see cref="IChunkVectorIndex"/>, a separate sqlite-vec-backed store) and stops anything still
    /// touching the project (an in-progress reindex; that project's <see cref="ProjectFileWatcher"/>,
    /// via <see cref="ProjectFileWatcherManager.StopWatching"/>). The shared, content-hash-keyed
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
        ProjectFileWatcherManager fileWatcherManager,
        CancellationToken ct)
    {
        var existing = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return Results.NotFound();
        }

        reindexRegistry.Cancel(id);
        fileWatcherManager.StopWatching(id);

        await vectorIndex.DeleteByProjectAsync(id, ct).ConfigureAwait(false);
        await projects.DeleteAsync(id, ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    internal static async Task<ProjectStatusDto?> BuildStatusDtoAsync(Guid id, IServiceProvider services, CancellationToken ct)
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
