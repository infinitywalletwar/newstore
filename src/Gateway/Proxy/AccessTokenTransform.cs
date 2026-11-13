using Duende.AccessTokenManagement.OpenIdConnect;

using Microsoft.AspNetCore.Authentication;

using NewStore.Gateway.Auth;

using Yarp.ReverseProxy.Transforms;

namespace NewStore.Gateway.Proxy;

/// <summary>
/// Decides which credential goes to the service. The session cookie never does: a cookie session becomes the user's access token,
/// a bearer request keeps its own token, and an anonymous request carries none.
/// </summary>
internal static class AccessTokenTransform
{
    public static async ValueTask ApplyAsync(RequestTransformContext transform)
    {
        var http = transform.HttpContext;
        transform.ProxyRequest.Headers.Remove("Cookie");

        if (GatewayAuthExtensions.IsBearerRequest(http.Request))
        {
            return; // YARP copied the Authorization header, and the service checks the token.
        }

        // Not a bearer request, so any other Authorization header is not for the service either.
        transform.ProxyRequest.Headers.Authorization = null;
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        // Returns the token from the session, or refreshes it first when it is about to expire.
        var result = await http.GetUserAccessTokenAsync(ct: http.RequestAborted);
        if (!result.WasSuccessful(out var token))
        {
            // Keycloak ended the session or the refresh token expired. The session is useless, so end it here too.
            await http.SignOutAsync(GatewayAuthExtensions.CookieScheme);
            // A status other than 200 makes YARP stop and send nothing to the service.
            await Problems.SessionExpired().ExecuteAsync(http);
            return;
        }

        transform.ProxyRequest.Headers.Authorization = new("Bearer", token.AccessToken.ToString());
    }
}
