using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NewStore.ServiceDefaults.Security;

public static class AuthExtensions
{
    /// <summary>
    /// Validates JWT bearer tokens from Keycloak: signature (keys from <see cref="AuthOptions.MetadataAddress"/>), issuer, audience
    /// and lifetime. Adds <see cref="Policies.SignedIn"/>. The service still needs <c>UseAuthentication</c>, <c>UseAuthorization</c>
    /// and a policy on each endpoint.
    /// </summary>
    public static AuthorizationBuilder AddNewStoreAuth(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience), "Auth:Issuer and Auth:Audience are required.")
            .ValidateOnStart();

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        // Configured from AuthOptions in DI, so the values are read when the first request needs them.
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) =>
            {
                var options = auth.Value;
                jwt.MetadataAddress = options.MetadataAddress ?? $"{options.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
                jwt.TokenValidationParameters.ValidAudience = options.Audience;
            });

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, SignedInHandler>());
        return builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SignedIn, policy => policy.RequireAuthenticatedUser().AddRequirements(new SignedInRequirement()));
    }

    /// <summary>
    /// Adds <see cref="Policies.Client"/>(<paramref name="clientId"/>): <c>azp</c> equals the client. Service clients have service
    /// accounts only and user clients have none, so <c>azp</c> alone tells a service's call from a user's.
    /// </summary>
    public static AuthorizationBuilder AddClientPolicy(this AuthorizationBuilder auth, string clientId) =>
        auth.AddPolicy(Policies.Client(clientId), policy => policy.RequireAuthenticatedUser().RequireClaim("azp", clientId));
}
