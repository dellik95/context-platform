using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>/api/hooks/status</c> — read side of the Dashboard's "Hook invocations" panel: how many times
/// the Claude Code lifecycle hooks (<c>Hooks/HookEndpoints.cs</c>) have fired for a project, and the
/// raw payload of the last 10 calls. Named distinctly from <c>HookEndpoints</c>/<c>MapHookEndpoints</c>
/// (the hook handlers themselves) to avoid confusion between "receiving a hook" and "reporting on
/// hooks received".
/// </summary>
public static class HooksStatusEndpoints
{
    private const int RecentLimit = 10;

    public static void MapHooksStatusEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireBearerToken().RequireMatchingProjectId();
        group.MapGet("/api/hooks/status", StatusAsync);
    }

    private static async Task<IResult> StatusAsync(
        Guid? projectId, IHookInvocationRepository repo, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        // Raw hook payloads carry prompts and file paths, so the project they belong to is the
        // caller's own -- never whichever id the query string names.
        var scopedProjectId = projectId ?? currentProject.ProjectId;

        var count = await repo.CountAsync(scopedProjectId, ct).ConfigureAwait(false);
        var recent = await repo.GetRecentAsync(scopedProjectId, RecentLimit, ct).ConfigureAwait(false);
        return Results.Ok(new HookStatusDto(count, [.. recent.Select(HookInvocationDto.FromEntity)]));
    }
}

public sealed record HookInvocationDto(Guid Id, HookType Type, string PayloadJson, DateTimeOffset ReceivedAt)
{
    public static HookInvocationDto FromEntity(HookInvocation h) => new(h.Id, h.Type, h.PayloadJson, h.ReceivedAt);
}

public sealed record HookStatusDto(int Count, HookInvocationDto[] Recent);
