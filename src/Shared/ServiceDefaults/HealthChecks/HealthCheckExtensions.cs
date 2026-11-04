using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NewStore.ServiceDefaults.HealthChecks;

public static class HealthCheckExtensions
{
    /// <summary>Checks with this tag run on the liveness endpoint.</summary>
    public const string LiveTag = "live";

    /// <summary>
    /// Adds the PostgreSQL readiness check. It reports Unhealthy on failure and gives up after
    /// 3 seconds, so a hanging database cannot hang the probe.
    /// </summary>
    public static IHealthChecksBuilder AddPostgres(this IHealthChecksBuilder builder) =>
        builder.AddCheck<PostgresHealthCheck>(
            "postgres",
            failureStatus: HealthStatus.Unhealthy,
            tags: null,
            timeout: TimeSpan.FromSeconds(3));
}