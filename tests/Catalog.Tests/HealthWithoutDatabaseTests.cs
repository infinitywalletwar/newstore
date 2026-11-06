using System.Diagnostics;
using System.Net.Sockets;

namespace NewStore.Catalog.Tests;

public class HealthWithoutDatabaseTests
{
    // Nothing listens on port 1, so the connection is refused at once.
    private const string RefusedDatabase =
        "Host=127.0.0.1;Port=1;Database=catalog;Username=catalog;Password=catalog;Timeout=2";

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
        // A listener that is started but never accepts: the kernel completes the TCP handshake, then nothing
        // answers, so Npgsql waits for the server's first message until the health pool's own connect Timeout
        // (3 seconds, set in AddPostgres) ends it. Timeout=30 stays in this connection string, so only that
        // override can end the wait in time.
        using var silentServer = new TcpListener(IPAddress.Loopback, 0);
        silentServer.Start();
        var port = ((IPEndPoint)silentServer.LocalEndpoint).Port;
        await using var factory = FactoryFor(
            $"Host=127.0.0.1;Port={port};Database=catalog;Username=catalog;Password=catalog;Timeout=30;SSL Mode=Disable");
        using var client = factory.CreateClient();

        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetAsync("/health/ready", ct);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Service_keeps_serving_when_collector_is_unreachable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = FactoryFor(RefusedDatabase);
        factory.Settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live", ct);
        var missing = await client.GetAsync("/no-such-page", ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
