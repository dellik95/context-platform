using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// Gates one path prefix (the SignalR hub) with <see cref="BearerTokenAuth.TryResolveProjectAsync"/>
/// via plain middleware rather than an endpoint filter. <c>MapHub&lt;T&gt;</c> returns a SignalR-specific
/// <c>HubEndpointConventionBuilder</c> — confirmed by reflection against the real
/// <c>Microsoft.AspNetCore.SignalR</c> package to NOT implement <c>RouteHandlerBuilder</c>/
/// <c>RouteGroupBuilder</c> — so endpoint filters aren't a reliable fit there the way they are for
/// Minimal API routes and <c>MapMcp</c>. Middleware sidesteps the question entirely: it runs before
/// routing selects an endpoint at all.
/// </summary>
public static class HubBearerTokenAuthMiddleware
{
    public static IApplicationBuilder UseHubBearerTokenAuth(this IApplicationBuilder app, PathString hubPath)
    {
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(hubPath))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var projects = context.RequestServices.GetRequiredService<IProjectRepository>();
            var project = await BearerTokenAuth.TryResolveProjectAsync(context, projects, context.RequestAborted).ConfigureAwait(false);
            if (project is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Items[CurrentProjectAccessor.HttpContextItemKey] = project;

            await next(context).ConfigureAwait(false);
        });
    }
}
