using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Api;

public sealed record SessionDto(Guid Id, string? ClaudeSessionId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Status)
{
    public static SessionDto FromEntity(Context.Domain.Entities.Session session) =>
        new(session.Id, session.ClaudeSessionId, session.StartedAt, session.EndedAt, session.Status.ToString());
}

public sealed record SessionListItemDto(SessionDto Session, string? Title)
{
    public static SessionListItemDto FromEntity(Context.Domain.Entities.Session session, string? title) =>
        new(SessionDto.FromEntity(session), title);
}

public sealed record SessionDetailDto(
    SessionDto Session,
    string? Title,
    string? Completed,
    string? Discovered,
    string? Decisions,
    string? ChangedFilesJson,
    string? OpenIssues,
    string? NextSteps,
    int EventCount);

public sealed record SessionSummaryChunkDto(
    Guid Id,
    Guid SessionId,
    int SequenceNumber,
    DateTimeOffset FromEventAt,
    DateTimeOffset ToEventAt,
    int EventCount,
    string? Title,
    string? Completed,
    string? Discovered,
    string? Decisions,
    string? FilesReadJson,
    string? ChangedFilesJson,
    string? OpenIssues,
    string? NextSteps,
    DateTimeOffset GeneratedAt)
{
    public static SessionSummaryChunkDto FromEntity(Context.Domain.Entities.SessionSummaryChunk chunk) =>
        new(chunk.Id, chunk.SessionId, chunk.SequenceNumber, chunk.FromEventAt, chunk.ToEventAt, chunk.EventCount,
            chunk.Title, chunk.Completed, chunk.Discovered, chunk.Decisions,
            chunk.FilesReadJson, chunk.ChangedFilesJson, chunk.OpenIssues, chunk.NextSteps,
            chunk.GeneratedAt);
}

/// <summary><c>/api/sessions</c> — read-only session/summary browsing for the Dashboard.</summary>
public static class SessionEndpoints
{
    private const int DefaultListLimit = 50;

    public static void MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sessions").RequireBearerToken();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/summary-chunks", GetSummaryChunksAsync);
    }

    private static async Task<IResult> ListAsync(int? limit, CurrentProjectAccessor currentProject, ISessionRepository sessions, CancellationToken ct)
    {
        var results = await sessions.ListByProjectAsync(currentProject.ProjectId, limit ?? DefaultListLimit, ct).ConfigureAwait(false);
        return Results.Ok(results.Select(r => SessionListItemDto.FromEntity(r.Session, r.Title)));
    }

    private static async Task<IResult> GetAsync(Guid id, ISessionRepository sessions, CurrentProjectAccessor project, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(project.ProjectId, id, ct).ConfigureAwait(false);
        if (session is null)
        {
            return Results.NotFound();
        }

        var summary = await sessions.GetSummaryAsync(project.ProjectId, id, ct).ConfigureAwait(false);
        var events = await sessions.GetEventsAsync(project.ProjectId, id, ct).ConfigureAwait(false);

        return Results.Ok(new SessionDetailDto(
            SessionDto.FromEntity(session),
            summary?.Title,
            summary?.Completed, summary?.Discovered, summary?.Decisions,
            summary?.ChangedFilesJson, summary?.OpenIssues, summary?.NextSteps,
            events.Count));
    }

    private static async Task<IResult> GetSummaryChunksAsync(Guid id, ISessionRepository sessions, CurrentProjectAccessor project, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(project.ProjectId, id, ct).ConfigureAwait(false);
        if (session is null)
        {
            return Results.NotFound();
        }

        var chunks = await sessions.GetSummaryChunksAsync(project.ProjectId, id, ct).ConfigureAwait(false);
        return Results.Ok(chunks.Select(SessionSummaryChunkDto.FromEntity));
    }
}
