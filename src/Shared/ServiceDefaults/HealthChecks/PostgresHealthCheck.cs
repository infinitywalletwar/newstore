using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace NewStore.ServiceDefaults.HealthChecks;

/// <summary>Readiness check: can this service run a query on its database right now?</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The token fires when the registration's timeout elapses, which cancels a hanging connect.
        // Exceptions are not caught here: the health check service turns them into the
        // registration's failure status, and the default writer prints only that status.
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}