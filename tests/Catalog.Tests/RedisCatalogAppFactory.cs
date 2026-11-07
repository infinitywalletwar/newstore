using Testcontainers.Redis;

namespace NewStore.Catalog.Tests;

/// <summary>The seeded Catalog with a real Redis container as the cache's L2. Each instance owns its container.</summary>
public sealed class RedisCatalogAppFactory(PostgresServer postgres, KafkaServer kafka) : SeededCatalogAppFactory(postgres, kafka)
{
    public RedisContainer Redis { get; } = new RedisBuilder("redis:8.10.3").Build();

    protected override async ValueTask ConfigureSettingsAsync()
    {
        await Redis.StartAsync(TestContext.Current.CancellationToken);
        Settings["ConnectionStrings:Redis"] = Redis.GetConnectionString();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Redis.DisposeAsync();
    }
}
