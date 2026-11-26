using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NewStore.Delivery.Courier;
using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;

using Npgsql;

namespace NewStore.Delivery.Tests;

/// <summary>Shared by the courier's test classes: what a shipment's row and its expected messages look like.</summary>
internal static class Trips
{
    public static Task<Shipment> RowAsync(this DeliveryFixture fixture, Guid id) =>
        fixture.QueryAsync(db => db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id, TestContext.Current.CancellationToken));

    /// <summary>The message any courier must send for <paramref name="seq"/> of <paramref name="shipment"/>'s trip.</summary>
    public static CourierPosition Expected(Shipment shipment, int seq, CourierOptions options)
    {
        var point = CourierTimeline.At(
            seq,
            shipment.CourierStartedAt!.Value,
            new GeoPoint(shipment.WarehouseLatitude, shipment.WarehouseLongitude),
            new GeoPoint(shipment.Latitude, shipment.Longitude),
            options);
        return new CourierPosition
        {
            ShipmentId = shipment.Id,
            Seq = point.Seq,
            Status = point.Status.ToString(),
            Latitude = point.Position.Latitude,
            Longitude = point.Position.Longitude,
            RecordedAt = point.RecordedAt,
        };
    }
}

/// <summary>
/// Runners driven by hand on <see cref="CourierFixture"/>: no courier ticks unless a test calls it. The role is Api, so its consumer
/// also makes the shipments that <c>ShipmentRequestedTests</c> publish to the shared <c>orders.orders</c>; a runner may claim those
/// too, so the tests assert only on their own shipments.
/// </summary>
public sealed class CourierTests(CourierFixture courier) : IClassFixture<CourierFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly PositionsTopic _topic = new(courier.Kafka.BootstrapServers);

    [Fact]
    public async Task Lost_lease_is_taken_over_and_the_seqs_repeat()
    {
        await courier.CloseOpenShipmentsAsync();
        var shipment = await courier.CreateShipmentAsync();
        var a = courier.NewRunner();
        var b = courier.NewRunner();
        var tick = courier.Courier.TickInterval;

        // a claims the trip at t = 0 and reports seqs 0, 1 and 2. It renews at t = 2 s, so its lease lasts until t = 11 s.
        await a.TickAsync(Ct);
        courier.Clock.Advance(tick);
        await a.TickAsync(Ct);
        courier.Clock.Advance(tick);
        await a.TickAsync(Ct);
        Assert.Contains(shipment.Id, a.Held);
        await _topic.WaitForAsync(shipment.Id, ps => ps.Any(p => p.Seq == 2), "a's seq 2");

        // a dies without releasing. Past its lease (t = 13 s) b claims the trip, knowing nothing of what a sent.
        courier.Clock.Advance(courier.Courier.Lease);
        await b.TickAsync(Ct);

        var row = await courier.RowAsync(shipment.Id);
        var last = CourierTimeline.LastSeq(courier.Courier);
        var positions = await _topic.WaitForAsync(shipment.Id, ps => ps.Any(p => p.Seq == last), "b's last seq");

        // b's message is the one a would have sent at that seq: same trip start, same point, same time.
        Assert.Equal(Trips.Expected(row, last, courier.Courier), Assert.Single(positions, p => p.Seq == last));
        // Every copy of every seq, whichever courier sent it, is the timeline's point for that seq, so no two copies differ.
        Assert.All(positions, p => Assert.Equal(Trips.Expected(row, p.Seq, courier.Courier), p));
        Assert.NotNull(row.CourierFinishedAt);
        Assert.DoesNotContain(shipment.Id, b.Held);

        // a comes back: its renewal finds the trip isn't its own any more, so it drops it and sends nothing.
        await a.TickAsync(Ct);
        Assert.DoesNotContain(shipment.Id, a.Held);
        Assert.Equal(positions.Count, (await _topic.ReadAsync(shipment.Id)).Count);
    }

    [Fact]
    public async Task Two_couriers_split_new_shipments()
    {
        await courier.CloseOpenShipmentsAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            created.Add((await courier.CreateShipmentAsync()).Id);
        }

        var a = courier.NewRunner(claimsPerTick: 2);
        var b = courier.NewRunner(claimsPerTick: 2);
        for (var turn = 0; turn < 20 && !created.All(id => a.Held.Contains(id) || b.Held.Contains(id)); turn++)
        {
            await (turn % 2 == 0 ? a : b).TickAsync(Ct);
        }

        Assert.NotEmpty(a.Held.Intersect(created));
        Assert.NotEmpty(b.Held.Intersect(created));
        Assert.Empty(a.Held.Intersect(b.Held));
        Assert.Equal(created.Order(), a.Held.Concat(b.Held).Intersect(created).Order());
    }
}

/// <summary>Role <c>Courier</c>: the app's own <see cref="CourierService"/> ticks when the test moves the clock.</summary>
public sealed class RunningCourierTests(RunningCourierFixture courier) : IClassFixture<RunningCourierFixture>
{
    [Fact]
    public async Task Courier_reports_a_position_every_tick_until_delivered()
    {
        await courier.CloseOpenShipmentsAsync();
        var shipment = await courier.CreateShipmentAsync();
        var topic = new PositionsTopic(courier.Kafka.BootstrapServers);
        var last = CourierTimeline.LastSeq(courier.Courier);

        // One tick at a time, and the next only once this tick's position is on the topic, so no two ticks run into one.
        for (var seq = 0; seq <= last; seq++)
        {
            courier.Clock.Advance(courier.Courier.TickInterval);
            await topic.WaitForAsync(shipment.Id, ps => ps.Any(p => p.Seq == seq), $"seq {seq}");
        }

        await DeliveryFixture.WaitAsync(
            async () => (await courier.RowAsync(shipment.Id)).CourierFinishedAt is not null, "the trip is finished");

        var positions = await topic.ReadAsync(shipment.Id);
        Assert.Equal([0, 1, 2, 3, 4, 5], positions.Select(p => p.Seq));
        Assert.Equal(
            ["Preparing", "Preparing", "PickedUp", "InTransit", "InTransit", "Delivered"],
            positions.Select(p => p.Status));
        var row = await courier.RowAsync(shipment.Id);
        Assert.All(positions, p => Assert.Equal(Trips.Expected(row, p.Seq, courier.Courier), p));
        Assert.Null(row.CourierLockedBy);
    }

    [Fact]
    public async Task Courier_role_serves_no_api()
    {
        var shipments = await courier.BuyerClient(Guid.NewGuid()).GetAsync(new Uri("/me/shipments", UriKind.Relative), TestContext.Current.CancellationToken);
        var ready = await courier.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, shipments.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}

/// <summary>Stops its host, so it has a fixture of its own.</summary>
public sealed class CourierStopTests(RunningCourierFixture courier) : IClassFixture<RunningCourierFixture>
{
    [Fact]
    public async Task Stopping_the_courier_releases_its_leases()
    {
        var ct = TestContext.Current.CancellationToken;
        await courier.CloseOpenShipmentsAsync();
        var shipment = await courier.CreateShipmentAsync();
        courier.Clock.Advance(courier.Courier.TickInterval);
        await new PositionsTopic(courier.Kafka.BootstrapServers)
            .WaitForAsync(shipment.Id, ps => ps.Any(p => p.Seq == 0), "the trip has started");

        var runner = courier.Services.GetRequiredService<CourierRunner>();
        Assert.Contains(shipment.Id, runner.Held);

        // Mid-trip, the host shuts down, as `docker compose stop` would make it. Program's app.Run() then returns and disposes the
        // app's services, so the row is read over a connection of the test's own.
        await courier.Services.GetRequiredService<IHost>().StopAsync(ct);

        await using var connection = new NpgsqlConnection(courier.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT courier_locked_until, courier_finished_at FROM shipments WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", shipment.Id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        var lockedUntil = reader.GetFieldValue<DateTimeOffset>(0);
        Assert.True(await reader.IsDBNullAsync(1, ct), "the trip is not finished");
        Assert.True(lockedUntil <= courier.Clock.GetUtcNow(), $"locked until {lockedUntil:O}, now {courier.Clock.GetUtcNow():O}");
        Assert.Empty(runner.Held);
    }
}

/// <summary>Pauses the broker, so it gets a Kafka of its own (<c>IClassFixture</c>) and cannot disturb the other tests.</summary>
public sealed class CourierOutageTests(PostgresServer postgres, CourierOutageTests.OwnKafkaServer kafka)
    : IClassFixture<CourierOutageTests.OwnKafkaServer>
{
    /// <summary>A second container, so the assembly's shared <see cref="KafkaServer"/> is not the one that gets paused.</summary>
    public sealed class OwnKafkaServer : KafkaServer;

    /// <summary>A producer that gives up after 1 s, so a frozen broker fails a send quickly.</summary>
    private sealed class OutageFixture(PostgresServer postgres, KafkaServer kafka) : CourierFixture(postgres, kafka)
    {
        protected override void ConfigureSettings(Dictionary<string, string?> settings)
        {
            base.ConfigureSettings(settings);
            settings["Messaging:ProduceTimeout"] = "00:00:01";
        }
    }

    [Fact]
    public async Task Delivered_waits_for_the_ack_before_the_run_is_finished()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var courier = new OutageFixture(postgres, kafka);
        await courier.InitializeAsync();
        var shipment = await courier.CreateShipmentAsync();
        var runner = courier.NewRunner();
        var topic = new PositionsTopic(kafka.BootstrapServers);
        var last = CourierTimeline.LastSeq(courier.Courier);

        // A fresh broker may take longer than 1 s to acknowledge its first message, so tick until seq 0 is on the topic.
        await DeliveryFixture.WaitAsync(
            async () =>
            {
                await runner.TickAsync(ct);
                return (await topic.ReadAsync(shipment.Id)).Any(p => p.Seq == 0);
            },
            "the first position is acknowledged");

        await kafka.PauseAsync(ct);
        try
        {
            // Past the trip's end: this tick sends Delivered, gets no acknowledgement, and must not finish the trip.
            courier.Clock.Advance(courier.Courier.PrepareTime + courier.Courier.TravelTime);
            await runner.TickAsync(ct);

            Assert.Null((await courier.RowAsync(shipment.Id)).CourierFinishedAt);
            Assert.Contains(shipment.Id, runner.Held);
        }
        finally
        {
            await kafka.UnpauseAsync(ct);
        }

        // The next ticks send Delivered again; the first one Kafka acknowledges finishes the trip.
        await DeliveryFixture.WaitAsync(
            async () =>
            {
                await runner.TickAsync(ct);
                return (await courier.RowAsync(shipment.Id)).CourierFinishedAt is not null;
            },
            "the trip is finished after the broker is back");

        var delivered = Assert.Single((await topic.ReadAsync(shipment.Id)).Where(p => p.Seq == last).Distinct());
        Assert.Equal("Delivered", delivered.Status);
        Assert.Empty(runner.Held);
    }
}
