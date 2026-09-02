using Context.Host.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Host.Api;

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

/// <summary><c>/api/sessions</c> — read-only session/summary browsing for the Dashboard.</summary>
public static class SessionEndpoints
{
    private const int DefaultListLimit = 50;

    public static void MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sessions").RequireBearerToken();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
    }

    private static async Task<IResult> ListAsync(Guid projectId, int? limit, ISessionRepository sessions, CancellationToken ct)
    {
        var results = await sessions.ListByProjectAsync(projectId, limit ?? DefaultListLimit, ct).ConfigureAwait(false);
        return Results.Ok(results.Select(r => SessionListItemDto.FromEntity(r.Session, r.Title)));
    }

    private static async Task<IResult> GetAsync(Guid id, ISessionRepository sessions, CancellationToken ct)
    {
        var session = await sessions.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (session is null)
        {
            return Results.NotFound();
        }

        var summary = await sessions.GetSummaryAsync(id, ct).ConfigureAwait(false);
        var events = await sessions.GetEventsAsync(id, ct).ConfigureAwait(false);

        return Results.Ok(new SessionDetailDto(
            SessionDto.FromEntity(session),
            summary?.Title,
            summary?.Completed, summary?.Discovered, summary?.Decisions,
            summary?.ChangedFilesJson, summary?.OpenIssues, summary?.NextSteps,
            events.Count));
    }
}
