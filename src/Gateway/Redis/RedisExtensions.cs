using Microsoft.Extensions.Diagnostics.HealthChecks;

using StackExchange.Redis;

namespace NewStore.Gateway.Redis;

public static class RedisExtensions
{
    private const int RedisTimeoutMilliseconds = 1000;
    private const int ProbeBudgetSeconds = 3;

    /// <summary>
    /// Registers the one Redis connection the gateway shares (sessions and Data Protection keys) and a readiness check that reports
    /// Degraded, never Unhealthy, when Redis does not answer: anonymous browsing still works without it.
    /// </summary>
    public static IHostApplicationBuilder AddGatewayRedis(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException("ConnectionStrings:Redis is not set.");

        // Do not fail startup when Redis is down, and give up on a command after a second.
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = RedisTimeoutMilliseconds;
        options.SyncTimeout = RedisTimeoutMilliseconds;
        options.AsyncTimeout = RedisTimeoutMilliseconds;

        // One connection, made on first use and handed to the container as a factory result, so the container disposes it.
        var connection = new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(options));
        builder.Services.AddSingleton(_ => connection.Value);
        builder.Services.AddSingleton(new RedisConnectionHolder(connection));

        builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>(
            "redis", HealthStatus.Degraded, tags: null, timeout: TimeSpan.FromSeconds(ProbeBudgetSeconds));
        return builder;
    }
}

/// <summary>Lets Data Protection, which is configured before the container exists, reach the same connection.</summary>
internal sealed record RedisConnectionHolder(Lazy<IConnectionMultiplexer> Connection);

/// <summary>Readiness check: does Redis answer PING?</summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy($"PING took {latency.TotalMilliseconds:F0} ms.");
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The registration's failureStatus turns this into Degraded.
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis does not answer PING.", ex);
        }
    }
}
