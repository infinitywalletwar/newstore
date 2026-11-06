using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using NewStore.ServiceDefaults.HealthChecks;
using NewStore.ServiceDefaults.Telemetry;

namespace NewStore.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    /// <summary>Registers what every service shares. The "self" liveness check and telemetry.</summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // Liveness answers "is the process running?", so it checks nothing external.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [HealthCheckExtensions.LiveTag]);

        builder.AddDefaultTelemetry();

        return builder;
    }

    /// <summary>/health/live runs only checks tagged "live"; /health/ready runs every check.</summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HealthCheckExtensions.LiveTag),
        });
        app.MapHealthChecks("/health/ready");

        return app;
    }
}
