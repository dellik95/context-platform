using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// Authorization for the project id a request names, as opposed to <see cref="BearerTokenAuth"/>,
/// which only answers "is this <em>a</em> valid token?". On a multi-tenant host those are different
/// questions: without this, project A's bearer token could read and write project B's settings,
/// memories, graph and job queue simply by naming B's id in the route or query string. A project id
/// a caller supplies is an <em>argument</em>, never a credential.
/// <para/>
/// Two shapes, because project-scoped routes name the project two different ways:
/// <list type="bullet">
/// <item><see cref="RequireOwnProjectFilter"/> — the project id is the <c>{id:guid}</c> route value
/// (<c>/api/projects/{id}/...</c>). Replaces bearer auth on those routes rather than stacking on
/// top of it, because it also accepts the admin token, which may act on any project.</item>
/// <item><see cref="RequireMatchingProjectIdFilter"/> — the project id is a <c>projectId</c> query
/// parameter. Stacks after <c>.RequireBearerToken()</c> and validates the value against the project
/// that token already resolved to.</item>
/// </list>
/// </summary>
public static class ProjectScopeAuth
{
    private const string RouteValueName = "id";
    private const string QueryParamName = "projectId";

    private const string ForbiddenMessage =
        "This bearer token does not authorize access to the requested project.";

    /// <summary>
    /// Authorized when the caller's bearer token resolves to the very project named by the route's
    /// <c>id</c> value, or — when <paramref name="allowAdmin"/> — the admin token is presented. A
    /// bearer token that resolves to a <em>different</em> project is rejected. On success the
    /// resolved <see cref="Project"/> is stashed for <see cref="CurrentProjectAccessor"/>, exactly
    /// as <see cref="BearerTokenAuth.RequireBearerTokenFilter"/> does (admin-token callers resolve
    /// no project, so handlers that admit them must read the route id, not the accessor).
    /// </summary>
    public static async ValueTask<object?> RequireOwnProjectFilter(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next, bool allowAdmin)
    {
        var http = ctx.HttpContext;
        if (allowAdmin)
        {
            var adminToken = http.RequestServices.GetRequiredService<AdminTokenProvider>();
            if (AdminTokenAuth.IsAuthorized(http, adminToken))
            {
                return await next(ctx).ConfigureAwait(false);
            }
        }

        var projects = http.RequestServices.GetRequiredService<IProjectRepository>();
        var project = await BearerTokenAuth.TryResolveProjectAsync(http, projects, http.RequestAborted).ConfigureAwait(false);
        if (project is null)
        {
            return Results.Unauthorized();
        }

        var requestedId = http.Request.RouteValues[RouteValueName] as string;
        if (!Guid.TryParse(requestedId, out var id) || id != project.Id)
        {
            return Results.Problem(ForbiddenMessage, statusCode: StatusCodes.Status403Forbidden);
        }

        http.Items[CurrentProjectAccessor.HttpContextItemKey] = project;

        return await next(ctx).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects a <c>?projectId=</c> that names a project other than the one the caller's bearer
    /// token resolved to. An absent or unparseable value is left alone — endpoints differ on what
    /// "no project" means (all projects, this project, host-wide rows), so that choice belongs to
    /// the handler; this filter only rules out impersonating another project.
    /// </summary>
    public static async ValueTask<object?> RequireMatchingProjectIdFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (http.Request.Query.TryGetValue(QueryParamName, out var raw)
            && Guid.TryParse(raw.ToString(), out var requestedId))
        {
            var current = http.RequestServices.GetRequiredService<CurrentProjectAccessor>();
            if (requestedId != current.ProjectId)
            {
                return Results.Problem(ForbiddenMessage, statusCode: StatusCodes.Status403Forbidden);
            }
        }

        return await next(ctx).ConfigureAwait(false);
    }
}

/// <summary>Chainable sugar over <see cref="ProjectScopeAuth"/>'s filters, mirroring <c>.RequireBearerToken()</c>.</summary>
public static class ProjectScopeAuthEndpointExtensions
{
    /// <summary>
    /// For <c>/api/projects/{id:guid}/...</c> routes. Use <em>instead of</em> <c>.RequireBearerToken()</c>.
    /// Pass <c>allowAdmin: false</c> on a route that consumes the caller's own token as a value
    /// rather than only as a credential — the admin token is not a project token and must not end
    /// up standing in for one.
    /// </summary>
    public static TBuilder RequireOwnProject<TBuilder>(this TBuilder builder, bool allowAdmin = true)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter((ctx, next) => ProjectScopeAuth.RequireOwnProjectFilter(ctx, next, allowAdmin));

    /// <summary>For routes taking <c>?projectId=</c>. Chain <em>after</em> <c>.RequireBearerToken()</c>.</summary>
    public static TBuilder RequireMatchingProjectId<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(ProjectScopeAuth.RequireMatchingProjectIdFilter);
}
