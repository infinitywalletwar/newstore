using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using StackExchange.Redis;

namespace NewStore.Catalog.Caching;

public static class CacheExtensions
{
    public static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(60);

    private const int RedisTimeoutMilliseconds = 1000;
    private const int ProbeBudgetSeconds = 3;

    /// <summary>
    /// Adds HybridCache: an in-process L1 in front of Redis as L2 when ConnectionStrings:Redis is set, L1 alone
    /// otherwise. Redis is a degraded dependency: the readiness check reports Degraded, never Unhealthy.
    /// </summary>
    public static IHostApplicationBuilder AddCatalogCache(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHybridCache(options =>
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = EntryLifetime,
                LocalCacheExpiration = EntryLifetime,
            });

        var connectionString = builder.Configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return builder;
        }

        // Do not fail startup when Redis is down, and give up on a command after a second.
        var redis = ConfigurationOptions.Parse(connectionString);
        redis.AbortOnConnectFail = false;
        redis.ConnectTimeout = RedisTimeoutMilliseconds;
        redis.SyncTimeout = RedisTimeoutMilliseconds;

        builder.Services.AddStackExchangeRedisCache(options => options.ConfigurationOptions = redis);
        // Put the tolerant wrapper between HybridCache and Redis (see TolerantDistributedCache).
        builder.Services.Remove(builder.Services.Single(d => d.ServiceType == typeof(IDistributedCache)));
        builder.Services.AddSingleton<IDistributedCache>(services => new TolerantDistributedCache(
            new RedisCache(services.GetRequiredService<IOptions<RedisCacheOptions>>()),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<TolerantDistributedCache>>()));

        // A factory, not an instance: the container disposes what it creates, and not what it is handed
        // (https://learn.microsoft.com/dotnet/core/extensions/dependency-injection/guidelines#services-not-created-by-the-service-container).
        builder.Services.AddSingleton(_ => new RedisHealthCheck(redis));
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            "redis",
            services => services.GetRequiredService<RedisHealthCheck>(),
            failureStatus: HealthStatus.Degraded,
            tags: null,
            timeout: TimeSpan.FromSeconds(ProbeBudgetSeconds)));

        return builder;
    }
}
