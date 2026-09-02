namespace Context.Host.Security;

/// <summary>
/// Gates one path prefix (the SignalR hub) with <see cref="BearerTokenAuth.IsAuthorized"/> via
/// plain middleware rather than an endpoint filter. <c>MapHub&lt;T&gt;</c> returns a SignalR-specific
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

            var project = context.RequestServices.GetRequiredService<HostProjectContext>();
            if (!BearerTokenAuth.IsAuthorized(context, project))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }
}
