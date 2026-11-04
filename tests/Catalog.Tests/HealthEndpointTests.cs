using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace NewStore.Catalog.Tests;

public class HealthEndpointTests(PostgresCatalogAppFactory factory)
{
    [Fact]
    public async Task Live_returns_healthy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Ready_returns_healthy_when_database_is_reachable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Ready_stays_healthy_when_request_pool_is_exhausted()
    {
        var ct = TestContext.Current.CancellationToken;
        // A request pool of one connection, on the fixture's container.
        var csb = new NpgsqlConnectionStringBuilder(factory.Settings["ConnectionStrings:Catalog"])
        {
            MaxPoolSize = 1,
            Timeout = 30,
        };
        await using var busyApp = new CatalogAppFactory { Settings = { ["ConnectionStrings:Catalog"] = csb.ConnectionString } };
        using var client = busyApp.CreateClient();
        // Hold the only request connection, as a busy service would.
        await using var held = await busyApp.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health/ready", ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(ct));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }
}