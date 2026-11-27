using Microsoft.Extensions.Options;

namespace NewStore.Gateway.Auth;

public static class CsrfExtensions
{
    /// <summary>
    /// A request that the session cookie authenticates, to <c>/api/*</c> or <c>/auth/user</c>, must carry <c>X-CSRF: 1</c>. A form or
    /// an image on another site cannot add a header, and a script there that adds one needs a CORS preflight, which the gateway
    /// never approves. Bearer and anonymous requests have no cookie to abuse, so they need no header. Run it after <c>UseAuthentication</c>.
    /// <para>
    /// The one exception is a hub's WebSocket upgrade under <c>/api/hubs/</c>: a browser cannot add a header to a WebSocket, so the
    /// upgrade passes by its <c>Origin</c> instead, which the browser sets and no script can change, when that is one of
    /// <c>Gateway:PublicOrigins</c>. Any other Origin, or none, is refused (ADR 0023).
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseCsrfHeader(this IApplicationBuilder app)
    {
        var publicOrigins = app.ApplicationServices.GetRequiredService<IOptions<GatewayOptions>>().Value.PublicOrigins
            .Select(GatewayOptionsExtensions.NormalizeOrigin)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var path = request.Path;
            var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/auth/user");
            if (guarded
                && !GatewayAuthExtensions.IsBearerRequest(request)
                && context.User.Identity?.IsAuthenticated == true
                && Refusal(request, publicOrigins) is { } refusal)
            {
                await refusal.ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }

    /// <summary>The problem for a cookie request that fails its CSRF check, or null when it passes.</summary>
    private static IResult? Refusal(HttpRequest request, HashSet<string> publicOrigins) =>
        IsHubUpgrade(request)
            ? (HasPublicOrigin(request, publicOrigins) ? null : Problems.CsrfOrigin())
            : (request.Headers["X-CSRF"] == "1" ? null : Problems.Csrf());

    /// <summary>
    /// A WebSocket upgrade to a hub, told by its headers: the gateway has no WebSockets middleware (YARP tunnels the upgrade
    /// itself), so <c>HttpContext.WebSockets.IsWebSocketRequest</c> is always false here.
    /// </summary>
    private static bool IsHubUpgrade(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        && request.Path.StartsWithSegments("/api/hubs", out var hub) && hub.HasValue && hub.Value != "/"
        && string.Equals(request.Headers.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
        && request.Headers.Connection.ToString().Contains("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static bool HasPublicOrigin(HttpRequest request, HashSet<string> publicOrigins) =>
        request.Headers.Origin.ToString() is { Length: > 0 } origin
        && publicOrigins.Contains(GatewayOptionsExtensions.NormalizeOrigin(origin));
}
