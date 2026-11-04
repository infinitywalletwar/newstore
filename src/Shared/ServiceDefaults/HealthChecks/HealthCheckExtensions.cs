using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace NewStore.ServiceDefaults.HealthChecks;

public static class HealthCheckExtensions
{
    /// <summary>Checks with this tag run on the liveness endpoint.</summary>
    public const string LiveTag = "live";

    /// <summary>Key of the readiness check's own <see cref="NpgsqlDataSource"/>.</summary>
    public const string HealthDataSourceKey = "health";

    // One budget for the probe. It is the registration's timeout and the health pool's connect Timeout.
    private const int ProbeBudgetSeconds = 3;

    /// <summary>
    /// Adds the PostgreSQL readiness check. It runs on a connection pool of its own, so a request pool
    /// full of busy connections cannot turn a busy pod into an unready one. It reports Unhealthy on
    /// failure and gives up after the probe budget (3 seconds), so a hanging database cannot hang the probe.
    /// </summary>
    public static IHealthChecksBuilder AddPostgres(this IHealthChecksBuilder builder, string connectionString)
    {
        // Same database, separate pool: Npgsql pools per distinct connection string, and a keyed data
        // source owns its own pool anyway. Two connections are enough for one probe at a time.
        // Timeout (whole seconds) bounds opening a connection, including the startup handshake that
        // the registration's cancellation token does not reach (see PostgresHealthCheck).
        var healthConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 2,
            Timeout = ProbeBudgetSeconds,
        }.ConnectionString;
        builder.Services.AddNpgsqlDataSource(healthConnectionString, serviceKey: HealthDataSourceKey);

        return builder.Add(new HealthCheckRegistration(
            "postgres",
            services => new PostgresHealthCheck(services.GetRequiredKeyedService<NpgsqlDataSource>(HealthDataSourceKey)),
            failureStatus: HealthStatus.Unhealthy,
            tags: null,
            timeout: TimeSpan.FromSeconds(ProbeBudgetSeconds)));
    }
}