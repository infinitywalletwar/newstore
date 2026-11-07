using System.Diagnostics;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry.Trace;

namespace NewStore.Catalog.Tests;

public class HealthWithoutDatabaseTests(KafkaServer kafka)
{
    // Nothing listens on port 1, so the connection is refused at once.
    private const string RefusedDatabase =
        "Host=127.0.0.1;Port=1;Database=catalog;Username=catalog;Password=catalog;Timeout=2";

    // The host also starts the messaging services, which need a reachable broker. The database is the only dependency left broken.
    private NewStoreAppFactory<Program> FactoryFor(string connectionString)
    {
        var factory = new NewStoreAppFactory<Program> { Settings = { ["ConnectionStrings:Catalog"] = connectionString } };
        factory.UseKafka(kafka);
        return factory;
    }

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
        using var exportListener = new OtlpExportListener();
        await using var factory = FactoryFor(RefusedDatabase);
        factory.Settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live", ct);
        var missing = await client.GetAsync("/no-such-page", ct);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // ForceFlush returns true even though the collector refuses the connection: it reports that the
        // processors handed their batch to the exporter, not that the collector received it. The exporter
        // reports a failed attempt on its EventSource, after its retry delay, so poll for it.
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (exportListener.Failures == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        Assert.True(exportListener.Failures > 0, "The exporter never reported a failed export.");
    }
}
