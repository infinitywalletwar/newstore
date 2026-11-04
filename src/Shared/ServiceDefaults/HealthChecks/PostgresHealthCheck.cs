using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace NewStore.ServiceDefaults.HealthChecks;

/// <summary>Readiness check: can this service run a query on its database right now?</summary>
internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The token fires when the registration's timeout elapses. Npgsql honours it while connecting
        // over TCP, but not while it waits for the server's first reply (the startup handshake), so
        // WaitAsync makes this method return when the token fires. The abandoned attempt still ends
        // at the connection string's Timeout, and the pool reclaims its connection.
        // Exceptions are not caught here: the health check service turns them into the
        // registration's failure status, and the default writer prints only that status.
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken).WaitAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}