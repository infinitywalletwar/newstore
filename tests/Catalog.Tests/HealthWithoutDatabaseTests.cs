using System.Diagnostics;

namespace NewStore.Catalog.Tests;

public class HealthWithoutDatabaseTests
{
    // Nothing listens on port 1, so the connection is refused at once.
    private const string RefusedDatabase =
        "Host=127.0.0.1;Port=1;Database=catalog;Username=catalog;Password=catalog;Timeout=2";

    // A non-routable address: the connect attempt gets no answer, and Npgsql would wait 30 s.
    private const string HangingDatabase =
        "Host=10.255.255.1;Port=5432;Database=catalog;Username=catalog;Password=catalog;Timeout=30";

    private static CatalogAppFactory FactoryFor(string connectionString) =>
        new() { Settings = { ["ConnectionStrings:Catalog"] = connectionString } };

    [Fact]
    public async Task Ready_returns_unhealthy_when_database_refuses_connections()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = FactoryFor(RefusedDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Live_stays_healthy_when_database_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = FactoryFor(RefusedDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Ready_answers_within_five_seconds_when_database_hangs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = FactoryFor(HangingDatabase);
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health/ready", ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }
}