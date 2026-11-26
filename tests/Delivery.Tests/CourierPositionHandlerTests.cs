using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using NewStore.Delivery.Shipments;
using NewStore.Delivery.Tracking;

namespace NewStore.Delivery.Tests;

/// <summary>
/// Delivery with a <see cref="RecordingHubContext"/> in place of the hub's, and no consumer of the positions stream: the tests hand
/// the batches to the handler themselves, so nothing else reaches the database or the recorder.
/// </summary>
public sealed class HandlerFixture(PostgresServer postgres, KafkaServer kafka) : DeliveryFixture(postgres, kafka)
{
    internal RecordingHubContext Hub { get; } = new();

    protected override void ConfigureSettings(Dictionary<string, string?> settings) =>
        settings["Delivery:Tracking:ConsumePositions"] = "false";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<TrackingHub>>();
            services.AddSingleton<IHubContext<TrackingHub>>(Hub);
        });
    }
}

/// <summary>Builds positions and hands batches to <see cref="CourierPositionHandler"/>, as the stream consumer would.</summary>
internal static class Positions
{
    public static CourierPosition At(Guid shipmentId, int seq, DateTimeOffset start, ShipmentStatus status = ShipmentStatus.InTransit) => new()
    {
        ShipmentId = shipmentId,
        Seq = seq,
        Status = status.ToString(),
        Latitude = 52.4836 + (seq * 0.001),
        Longitude = 13.3897 + (seq * 0.001),
        RecordedAt = start.AddSeconds(seq * 2),
    };

    /// <summary>
    /// Runs the handler on one batch in a scope of <paramref name="app"/>. The handler is built here, not resolved: a host with
    /// <c>ConsumePositions=false</c> doesn't register it.
    /// </summary>
    public static async Task HandleAsync(DeliveryFixture app, params CourierPosition[] batch)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CourierPositionHandler>(scope.ServiceProvider);
        await handler.HandleAsync(batch, TestContext.Current.CancellationToken);
    }

    public static Task<List<int>> StoredSeqsAsync(DeliveryFixture app, Guid shipmentId) =>
        app.QueryAsync(db => db.ShipmentPositions.AsNoTracking()
            .Where(p => p.ShipmentId == shipmentId).OrderBy(p => p.Seq).Select(p => p.Seq)
            .ToListAsync(TestContext.Current.CancellationToken));

    public static Task<Shipment> ShipmentAsync(DeliveryFixture app, Guid shipmentId) =>
        app.QueryAsync(db => db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId, TestContext.Current.CancellationToken));
}

public sealed class CourierPositionHandlerTests(HandlerFixture delivery) : IClassFixture<HandlerFixture>
{
    private DateTimeOffset Start => Api.Micro(delivery.Clock.GetUtcNow());

    [Fact]
    public async Task Batch_stores_positions_and_moves_the_shipment_to_the_newest()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var start = Start;

        // Out of order within the batch: the shipment moves to the highest seq, not to the last one in the list.
        await Positions.HandleAsync(delivery,
            Positions.At(shipment.Id, 2, start, ShipmentStatus.PickedUp),
            Positions.At(shipment.Id, 0, start, ShipmentStatus.Preparing),
            Positions.At(shipment.Id, 1, start, ShipmentStatus.Preparing));

        Assert.Equal([0, 1, 2], await Positions.StoredSeqsAsync(delivery, shipment.Id));
        var newest = Positions.At(shipment.Id, 2, start, ShipmentStatus.PickedUp);
        var moved = await Positions.ShipmentAsync(delivery, shipment.Id);
        Assert.Equal(ShipmentStatus.PickedUp, moved.Status);
        Assert.Equal(2, moved.LastSeq);
        Assert.Equal(newest.Latitude, moved.LastLatitude);
        Assert.Equal(newest.Longitude, moved.LastLongitude);
        Assert.Equal(newest.RecordedAt, moved.LastPositionAt);
        Assert.Equal(newest.RecordedAt, moved.PickedUpAt);
        Assert.Null(moved.DeliveredAt);
    }

    [Fact]
    public async Task Batch_with_duplicates_and_stale_seqs_changes_nothing()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var start = Start;
        await Positions.HandleAsync(delivery, [.. Enumerable.Range(1, 5).Select(seq => Positions.At(shipment.Id, seq, start))]);
        var before = await Positions.ShipmentAsync(delivery, shipment.Id);
        var sentBefore = delivery.Hub.SentTo(shipment.Id).Count;

        // A replayed batch: Kafka delivers at least once, and a courier that took the shipment over repeats seqs.
        await Positions.HandleAsync(delivery,
            Positions.At(shipment.Id, 3, start), Positions.At(shipment.Id, 3, start), Positions.At(shipment.Id, 4, start));

        Assert.Equal([1, 2, 3, 4, 5], await Positions.StoredSeqsAsync(delivery, shipment.Id));
        var after = await Positions.ShipmentAsync(delivery, shipment.Id);
        Assert.Equal(5, after.LastSeq);
        Assert.Equal(before.LastPositionAt, after.LastPositionAt);
        Assert.Equal(sentBefore, delivery.Hub.SentTo(shipment.Id).Count);
    }

    [Fact]
    public async Task Pickup_and_delivery_times_are_set_once()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var start = Start;
        var pickedUp = Positions.At(shipment.Id, 1, start, ShipmentStatus.PickedUp);
        var delivered = Positions.At(shipment.Id, 3, start, ShipmentStatus.Delivered);

        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, start, ShipmentStatus.Preparing));
        Assert.Null((await Positions.ShipmentAsync(delivery, shipment.Id)).PickedUpAt);
        await Positions.HandleAsync(delivery, pickedUp);
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 2, start, ShipmentStatus.InTransit));
        await Positions.HandleAsync(delivery, delivered);
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 4, start, ShipmentStatus.Delivered));

        var moved = await Positions.ShipmentAsync(delivery, shipment.Id);
        Assert.Equal(pickedUp.RecordedAt, moved.PickedUpAt);
        Assert.Equal(delivered.RecordedAt, moved.DeliveredAt);
        Assert.Equal(ShipmentStatus.Delivered, moved.Status);
        Assert.Equal(4, moved.LastSeq);
    }

    [Fact]
    public async Task Positions_for_an_unknown_shipment_are_dropped()
    {
        // A topic replayed after a database reset holds positions of shipments this database never had. Throwing would retry the
        // batch for ever and stop the stream (Review Focus 4).
        var shipment = await delivery.CreateShipmentAsync();
        var unknown = Guid.CreateVersion7();
        var start = Start;

        await Positions.HandleAsync(delivery,
            Positions.At(unknown, 1, start), Positions.At(shipment.Id, 1, start), Positions.At(unknown, 2, start));

        Assert.Equal([1], await Positions.StoredSeqsAsync(delivery, shipment.Id));
        Assert.Empty(await Positions.StoredSeqsAsync(delivery, unknown));
        Assert.Empty(delivery.Hub.SentTo(unknown));
        var warning = Assert.Single(delivery.Logs.Snapshot(), l =>
            l.Category == typeof(CourierPositionHandler).FullName && l.Level == LogLevel.Warning && l.Message.Contains(unknown.ToString()));
        Assert.Contains("2 positions", warning.Message);
    }

    [Fact]
    public async Task Only_new_positions_are_sent()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var other = await delivery.CreateShipmentAsync();
        var start = Start;
        await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, start), Positions.At(shipment.Id, 1, start));

        await Positions.HandleAsync(delivery,
            Positions.At(shipment.Id, 3, start), Positions.At(other.Id, 0, start), Positions.At(shipment.Id, 1, start),
            Positions.At(shipment.Id, 2, start), Positions.At(shipment.Id, 3, start));

        Assert.Equal([0, 1, 2, 3], delivery.Hub.SentTo(shipment.Id).Select(p => p.Seq));
        Assert.Equal([0], delivery.Hub.SentTo(other.Id).Select(p => p.Seq));
        var message = Assert.Single(delivery.Hub.Sent, s => s.Group == TrackingHub.Group(other.Id));
        Assert.Equal(TrackingHub.PositionMethod, message.Method);
        Assert.Equal(Positions.At(other.Id, 0, start), message.Args.Single());
    }

    [Fact]
    public async Task Failed_send_does_not_fail_the_batch()
    {
        var shipment = await delivery.CreateShipmentAsync();
        var start = Start;
        delivery.Hub.Fail = true;
        try
        {
            await Positions.HandleAsync(delivery, Positions.At(shipment.Id, 0, start), Positions.At(shipment.Id, 1, start));
        }
        finally
        {
            delivery.Hub.Fail = false;
        }

        // The rows are stored: the next Track's snapshot gives them to the browser.
        Assert.Equal([0, 1], await Positions.StoredSeqsAsync(delivery, shipment.Id));
        Assert.Equal(1, (await Positions.ShipmentAsync(delivery, shipment.Id)).LastSeq);
        Assert.Contains(delivery.Logs.Snapshot(), l =>
            l.Category == typeof(CourierPositionHandler).FullName && l.Level == LogLevel.Warning && l.Exception is not null);
    }
}
