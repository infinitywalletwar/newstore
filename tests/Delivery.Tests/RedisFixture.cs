using Testcontainers.Redis;

namespace NewStore.Delivery.Tests;

/// <summary>One Redis, Compose's image, for the test class that runs two hosts on it.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8.10.3").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
