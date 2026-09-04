using Context.Domain.Entities;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// Scoped, per-request accessor for the <see cref="Project"/> that <see cref="BearerTokenAuth"/>
/// resolved the caller's bearer token to. <see cref="BearerTokenAuth.RequireBearerTokenFilter"/>
/// (REST/MCP routes) and <see cref="HubBearerTokenAuthMiddleware"/> (the SignalR hub) both stash
/// the resolved <see cref="Project"/> into <c>HttpContext.Items</c> under
/// <see cref="HttpContextItemKey"/> right after a successful lookup; this reads it back via
/// <see cref="IHttpContextAccessor"/> so anything deeper in the same request can ask "which
/// project is this?" without a <c>Project</c>/<c>projectId</c> parameter threaded through every
/// call in between.
/// <para/>
/// Only valid on a request that actually went through one of those two auth checks — reading
/// <see cref="Project"/>/<see cref="ProjectId"/> anywhere else (a route that skips bearer auth, or
/// outside an HTTP request entirely) throws.
/// </summary>
public sealed class CurrentProjectAccessor(IHttpContextAccessor httpContextAccessor)
{
    /// <summary>Key <see cref="BearerTokenAuth"/>/<see cref="HubBearerTokenAuthMiddleware"/> stash the resolved <see cref="Project"/> under in <c>HttpContext.Items</c>.</summary>
    public const string HttpContextItemKey = "Context.Dashboard.Server.Security.CurrentProject";

    public Project Project
    {
        get
        {
            var http = httpContextAccessor.HttpContext
                ?? throw new InvalidOperationException("CurrentProjectAccessor was used outside an HTTP request.");

            if (http.Items.TryGetValue(HttpContextItemKey, out var value) && value is Project project)
            {
                return project;
            }

            throw new InvalidOperationException(
                "CurrentProjectAccessor.Project was read on a request BearerTokenAuth/HubBearerTokenAuthMiddleware " +
                "never resolved a project for (e.g. a route that doesn't require a bearer token).");
        }
    }

    public Guid ProjectId => Project.Id;
}
