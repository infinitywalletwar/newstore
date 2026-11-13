using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Authentication;

namespace NewStore.Gateway.Auth;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/auth");
        auth.MapGet("/login", Login);
        auth.MapGet("/user", (Func<HttpContext, Task<IResult>>)User);
        auth.MapGet("/logout", Logout);
        // Everything else under /auth is not a page of the app.
        auth.Map("/{**rest}", () => Problems.NotFound());
        return app;
    }

    // Starts the sign-in. returnUrl is where the browser lands afterwards, so it must stay on this site.
    private static IResult Login(string? returnUrl)
    {
        returnUrl ??= "/";
        return IsLocalPath(returnUrl)
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [GatewayAuthExtensions.OidcScheme])
            : Problems.BadReturnUrl();
    }

    // Starts with one slash, not two, and not a slash and a backslash: browsers read "//host" and "/\host" as another site.
    // Control characters are refused too, because browsers drop tabs and line breaks inside a URL, so "/<tab>/host" becomes "//host".
    internal static bool IsLocalPath(string url) =>
        url.Length > 0 && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);

    // Never challenges: a signed-out browser gets isAuthenticated=false, and the app decides what to show.
    private static async Task<IResult> User(HttpContext http)
    {
        var session = await http.AuthenticateAsync(GatewayAuthExtensions.CookieScheme);
        if (!session.Succeeded || session.Principal is not { } user)
        {
            return Results.Ok(new { isAuthenticated = false });
        }

        // Only the user's identity. The tokens stay in the session on the server.
        return Results.Ok(new
        {
            isAuthenticated = true,
            sub = user.FindFirst("sub")?.Value,
            name = user.FindFirst("name")?.Value ?? user.FindFirst("preferred_username")?.Value,
            logoutUrl = $"/auth/logout?sid={Uri.EscapeDataString(user.FindFirst("sid")?.Value ?? "")}",
        });
    }

    // A GET that changes state, so another site could link to it. It needs the session's own id, which that site cannot know.
    private static async Task<IResult> Logout(HttpContext http, string? sid)
    {
        var session = await http.AuthenticateAsync(GatewayAuthExtensions.CookieScheme);
        if (!session.Succeeded)
        {
            return Results.LocalRedirect("/");
        }

        var expected = session.Principal!.FindFirst("sid")?.Value;
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(expected)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sid), Encoding.UTF8.GetBytes(expected)))
        {
            return Problems.BadSessionId();
        }

        // The cookie scheme drops the session from Redis, the OIDC scheme ends Keycloak's session and then returns to "/".
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [GatewayAuthExtensions.CookieScheme, GatewayAuthExtensions.OidcScheme]);
    }
}
