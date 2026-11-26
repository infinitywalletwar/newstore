using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;
using NewStore.Messaging;

namespace NewStore.Delivery.Tests;

/// <summary>Host A: holds the browser's connection and consumes no positions, as an instance whose partitions went elsewhere.</summary>
public sealed class HostA(PostgresServer postgres, KafkaServer kafka, string? redis) : DeliveryFixture(postgres, kafka)
{
    protected override void ConfigureSettings(Dictionary<string, string?> settings)
    {
        settings["Delivery:Tracking:ConsumePositions"] = "false";
        settings["ConnectionStrings:Redis"] = redis;
    }
}

/// <summary>Host B: A's database, A's consumer groups, and the positions stream. It doesn't migrate: A has.</summary>
public sealed class HostB(PostgresServer postgres, KafkaServer kafka, HostA a, string? redis) : DeliveryFixture(postgres, kafka)
{
    public override string ClientId => a.ClientId;

    protected override string? SharedDatabase => a.ConnectionString;

    protected override void ConfigureSettings(Dictionary<string, string?> settings) =>
        settings["ConnectionStrings:Redis"] = redis;
}

/// <summary>
/// Two Delivery instances, as Compose runs <c>delivery</c> and <c>delivery-b</c>: the browser's WebSocket is on one, and the
/// position may be consumed by the other. Only the Redis backplane carries it across.
/// </summary>
public sealed class TwoHostTests(PostgresServer postgres, KafkaServer kafka, RedisFixture redis) : IClassFixture<RedisFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Position_consumed_by_host_b_reaches_a_client_on_host_a()
    {
        await using var hosts = await StartAsync(redis.ConnectionString);
        var (position, positions) = await TrackOnAThenProduceAsync(hosts);

        // B stores it (the row appears), and A's client gets it through Redis.
        await PositionStreamTests.AdvanceUntilAsync(hosts.B,
            async () => (await Positions.StoredSeqsAsync(hosts.A, position.ShipmentId)).Contains(position.Seq), "host B stores the position");
        Assert.Equal(position, await positions.NextAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Without_redis_a_client_on_the_other_host_misses_the_position()
    {
        await using var hosts = await StartAsync(redis: null);
        var (position, positions) = await TrackOnAThenProduceAsync(hosts);

        await PositionStreamTests.AdvanceUntilAsync(hosts.B,
            async () => (await Positions.StoredSeqsAsync(hosts.A, position.ShipmentId)).Contains(position.Seq), "host B stores the position");

        // B sent it to its own connections only, and A's client is not one of them.
        await positions.AssertNoneAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(hosts.A.Logs.Snapshot(), l => l.Level == LogLevel.Warning && l.Message == "no Redis: SignalR runs without a backplane");
    }

    private async Task<Hosts> StartAsync(string? redis)
    {
        // A first: it migrates the database. B starts once A is ready and never migrates.
        var a = new HostA(postgres, kafka, redis);
        await a.InitializeAsync();
        var b = new HostB(postgres, kafka, a, redis);
        await b.InitializeAsync();
        return new Hosts(a, b);
    }

    private static async Task<(CourierPosition Position, Inbox<CourierPosition> Received)> TrackOnAThenProduceAsync(Hosts hosts)
    {
        var buyer = Guid.NewGuid();
        var shipment = await hosts.A.CreateShipmentAsync(buyer);
        var connection = await HubClient.ConnectAsync(hosts.A, TestTokens.User(buyer, "delivery"));
        hosts.Connections.Add(connection);
        var received = connection.Listen<CourierPosition>(TrackingHub.PositionMethod);
        await connection.InvokeAsync<ShipmentDetails>("Track", shipment.Id, Ct);

        var position = Positions.At(shipment.Id, 0, Api.Micro(hosts.A.Clock.GetUtcNow()), ShipmentStatus.Preparing);
        await hosts.A.Services.GetRequiredService<IStreamProducer>()
            .ProduceAsync(CourierPosition.Topic, shipment.Id.ToString(), position, Ct);
        return (position, received);
    }

    private sealed record Hosts(HostA A, HostB B) : IAsyncDisposable
    {
        public List<HubConnection> Connections { get; } = [];

        public async ValueTask DisposeAsync()
        {
            foreach (var connection in Connections)
            {
                await connection.DisposeAsync();
            }

            await B.DisposeAsync();
            await A.DisposeAsync();
        }
    }
}
