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
}