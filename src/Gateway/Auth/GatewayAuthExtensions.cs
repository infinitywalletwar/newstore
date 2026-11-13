using Duende.AccessTokenManagement;
using Duende.AccessTokenManagement.OpenIdConnect;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using NewStore.Gateway.Redis;
using NewStore.ServiceDefaults.Security;

using StackExchange.Redis;

namespace NewStore.Gateway.Auth;

public static class GatewayAuthExtensions
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = "oidc";

    /// <summary>The scheme the gateway authenticates every request with. It picks the cookie or the bearer token.</summary>
    public const string RequestScheme = "gateway";

    public const string SessionCookieName = "newstore-session";

    /// <summary>
    /// A request with <c>Authorization: Bearer</c> is a service or tool calling with a token. Anything else is a browser, and its
    /// credential is the session cookie.
    /// </summary>
    internal static bool IsBearerRequest(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    public static IHostApplicationBuilder AddGatewayAuth(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<OidcOptions>().BindConfiguration(OidcOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Authority) && !string.IsNullOrWhiteSpace(o.ClientId) && !string.IsNullOrWhiteSpace(o.ClientSecret),
                "Oidc:Authority, Oidc:ClientId and Oidc:ClientSecret are required.")
            .ValidateOnStart();

        // Keys and sessions are in Redis, so a restart or a second instance reads the same cookies. The application name keeps the
        // keys apart from any other app that shares the Redis.
        services.AddDataProtection().SetApplicationName("newstore-gateway");
        // What PersistKeysToStackExchangeRedis does, but with the connection taken from DI instead of built up front.
        services.AddOptions<KeyManagementOptions>().Configure<IConnectionMultiplexer>((keys, redis) =>
            keys.XmlRepository = new RedisXmlRepository(() => redis.GetDatabase(), "newstore:data-protection-keys"));
        services.AddSingleton<RedisTicketStore>();
        services.TryAddSingleton(TimeProvider.System);

        // Bearer requests: JwtBearer, from AddNewStoreAuth. The gateway only passes the token on, so it does not check the audience;
        // each service checks its own.
        builder.AddNewStoreAuth()
            // The gateway's signed-in policy means authenticated with a subject. A cookie session has no azp, so the client check
            // for bearer tokens is in the JwtBearer event below. This replaces the shared policy of the same name.
            .AddPolicy(Policies.SignedIn, policy => policy.RequireAuthenticatedUser().RequireClaim("sub"));
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                jwt.TokenValidationParameters.ValidateAudience = false;
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (context.Principal?.ClientId() is not { } client || !auth.Value.UserClients.Contains(client))
                        {
                            context.Fail("The token was not issued to a user client.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieScheme;
                options.DefaultAuthenticateScheme = RequestScheme;
                options.DefaultChallengeScheme = RequestScheme;
            })
            .AddPolicyScheme(RequestScheme, displayName: null, options =>
                options.ForwardDefaultSelector = context =>
                    IsBearerRequest(context.Request) ? JwtBearerDefaults.AuthenticationScheme : CookieScheme)
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = SessionCookieName;
                options.Cookie.HttpOnly = true;
                // Strict now, and no Secure flag until Phase 4 brings HTTPS (ADR 0015).
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                // Where a non-API challenge would go. Nothing outside /api is authorized today, so these are defaults kept honest.
                options.LoginPath = "/auth/login";
                options.ReturnUrlParameter = "returnUrl";
                options.Events.OnRedirectToLogin = context => ApiAware(context.HttpContext, Problems.SignInRequired(), () => context.Response.Redirect(context.RedirectUri));
                options.Events.OnRedirectToAccessDenied = context => ApiAware(context.HttpContext, Problems.Forbidden(), () => context.Response.Redirect(context.RedirectUri));
            })
            .AddOpenIdConnect(OidcScheme, _ => { });
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<RedisTicketStore>((cookie, store) => cookie.SessionStore = store);
        services.AddOptions<OpenIdConnectOptions>(OidcScheme).Configure<IOptions<OidcOptions>>(ConfigureOidc);

        // Refreshes the user's access token shortly before it expires. The challenge scheme is named because the default one is
        // the policy scheme above, which has no OpenID Connect options.
        services.AddOpenIdConnectAccessTokenManagement(options => options.ChallengeScheme = Scheme.Parse(OidcScheme));
        return builder;
    }

    private static void ConfigureOidc(OpenIdConnectOptions oidc, IOptions<OidcOptions> settings)
    {
        var options = settings.Value;
        oidc.SignInScheme = CookieScheme;
        oidc.Authority = options.Authority;
        oidc.MetadataAddress = options.MetadataAddress;
        oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;
        oidc.ClientId = options.ClientId;
        oidc.ClientSecret = options.ClientSecret;
        oidc.ResponseType = "code"; // the authorization code flow, with PKCE (the handler's default for it)
        oidc.UsePkce = true;
        oidc.SaveTokens = true;
        oidc.MapInboundClaims = false;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.TokenValidationParameters.NameClaimType = "name";

        // Until Phase 4 brings HTTPS: the handler's own cookies default to SameSite=None and Secure=Always, which a browser drops
        // over plain HTTP. Lax is sent on the redirect back from Keycloak. A Lax cookie is not sent on a cross-site form POST, which
        // is what the default response mode uses, so the code comes back in the query string.
        oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
        oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        oidc.NonceCookie.SameSite = SameSiteMode.Lax;
        oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;

        // Where Keycloak sends the browser after it ended its session: the app's start page. The handler's default is
        // /signout-callback-oidc, which the realm's client does not list as a post-logout redirect URI, so Keycloak refuses it.
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.PostLogoutRedirectUri = $"{context.Request.Scheme}://{context.Request.Host}/";
            return Task.CompletedTask;
        };

        var inner = oidc.Events.OnRedirectToIdentityProvider;
        oidc.Events.OnRedirectToIdentityProvider = async context =>
        {
            // Defence in depth: a challenge goes to the cookie scheme, so nothing reaches here for /api today. If a later change
            // challenges oidc on an API route, it still answers 401 instead of redirecting a fetch to Keycloak.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.HandleResponse();
                await Problems.SignInRequired().ExecuteAsync(context.HttpContext);
                return;
            }

            await inner(context);
        };
    }

    // A challenge on /api is a 401 problem, never a redirect: a fetch cannot follow a redirect to Keycloak's login page.
    // .NET 10's cookie handler answers 401 by itself only for endpoints it recognises as APIs (see the breaking-change note linked in
    // ADR 0015), and the test Anonymous_call_to_a_signed_in_route_is_401_not_a_redirect showed a YARP route is not one.
    private static Task ApiAware(HttpContext http, IResult apiReply, Action otherwise)
    {
        if (http.Request.Path.StartsWithSegments("/api"))
        {
            return apiReply.ExecuteAsync(http);
        }

        otherwise();
        return Task.CompletedTask;
    }
}
