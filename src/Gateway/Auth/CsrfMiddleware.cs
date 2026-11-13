namespace NewStore.Gateway.Auth;

public static class CsrfExtensions
{
    /// <summary>
    /// A request that the session cookie authenticates, to <c>/api/*</c> or <c>/auth/user</c>, must carry <c>X-CSRF: 1</c>. A form or
    /// an image on another site cannot add a header, and a script there that adds one needs a CORS preflight, which the gateway
    /// never approves. Bearer and anonymous requests have no cookie to abuse, so they need no header. Run it after <c>UseAuthentication</c>.
    /// </summary>
    public static IApplicationBuilder UseCsrfHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/auth/user");
            if (guarded
                && !GatewayAuthExtensions.IsBearerRequest(context.Request)
                && context.User.Identity?.IsAuthenticated == true
                && context.Request.Headers["X-CSRF"] != "1")
            {
                await Problems.Csrf().ExecuteAsync(context);
                return;
            }

            await next(context);
        });
}
