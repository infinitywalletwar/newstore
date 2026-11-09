using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NewStore.ServiceDefaults.Security;

/// <summary>
/// Both <c>AddNewStoreAuth</c> and <c>AddServiceClient</c> need <see cref="AuthOptions"/>. Binding the section twice would
/// append <see cref="AuthOptions.UserClients"/> twice, so each step runs once however often it is called.
/// </summary>
internal static class AuthOptionsRegistration
{
    private sealed class BoundMarker;

    private sealed class CallerValidationMarker;

    public static void Bind(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(BoundMarker)))
        {
            return;
        }

        services.AddSingleton<BoundMarker>();
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);
    }

    /// <summary>A service that validates tokens needs an issuer and an audience; the check runs at startup.</summary>
    public static void RequireTokenSettings(IServiceCollection services) =>
        services.AddOptions<AuthOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer) && !string.IsNullOrWhiteSpace(o.Audience),
                "Auth:Issuer and Auth:Audience are required.")
            .ValidateOnStart();

    /// <summary>A service that calls others needs its own client id, secret and token endpoint; the check runs at startup.</summary>
    public static void RequireCallerSettings(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(CallerValidationMarker)))
        {
            return;
        }

        services.AddSingleton<CallerValidationMarker>();
        services.AddOptions<AuthOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.ClientId)
                    && !string.IsNullOrWhiteSpace(o.ClientSecret)
                    && !string.IsNullOrWhiteSpace(o.TokenEndpoint),
                "Auth:ClientId, Auth:ClientSecret and Auth:TokenEndpoint are required for a service that calls other services.")
            .ValidateOnStart();
    }
}
