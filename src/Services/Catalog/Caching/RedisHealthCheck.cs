using Microsoft.Extensions.Diagnostics.HealthChecks;

using StackExchange.Redis;

namespace NewStore.Catalog.Caching;

/// <summary>
/// Readiness check: does Redis answer PING? It owns its connection, so a busy cache connection cannot make
/// the probe slow or fail. It is a singleton so the connection is made once; the multiplexer reconnects
/// by itself because <c>AbortOnConnectFail</c> is false.
/// </summary>
internal sealed class RedisHealthCheck(ConfigurationOptions options) : IHealthCheck, IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection = new(() => ConnectionMultiplexer.ConnectAsync(options));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _connection.Value.WaitAsync(cancellationToken);
            var latency = await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy($"PING took {latency.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The registration's failureStatus turns this into Degraded.
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis does not answer PING.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            await _connection.Value.Result.DisposeAsync();
        }
    }
}
