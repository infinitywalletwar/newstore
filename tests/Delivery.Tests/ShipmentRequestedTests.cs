using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using NewStore.Contracts.Orders;
using NewStore.Delivery.Shipments;
using NewStore.Messaging.Inbox;

namespace NewStore.Delivery.Tests;

public sealed class ShipmentRequestedTests(DeliveryFixture delivery) : IClassFixture<DeliveryFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task PublishAsync(ShipmentRequested request) =>
        delivery.Kafka.PublishAsync(OrderEvents.Topic, request.OrderId.ToString(), request, Ct);

    private Task<List<Shipment>> ShipmentsOfAsync(Guid orderId) =>
        delivery.QueryAsync(db => db.Shipments.AsNoTracking().Where(s => s.OrderId == orderId).ToListAsync(Ct));

    private Task<bool> HandledAsync(Guid eventId) =>
        delivery.QueryAsync(db => db.Set<InboxMessage>().AnyAsync(m => m.MessageId == eventId, Ct));

    [Fact]
    public async Task ShipmentRequested_creates_a_preparing_shipment_with_the_warehouse()
    {
        var request = DeliveryFixture.NewShipmentRequested(Guid.NewGuid());

        await PublishAsync(request);
        await DeliveryFixture.WaitAsync(() => HandledAsync(request.EventId), "the event is handled");

        var shipment = Assert.Single(await ShipmentsOfAsync(request.OrderId));
        Assert.NotEqual(Guid.Empty, shipment.Id);
        Assert.Equal(request.BuyerId, shipment.BuyerId);
        Assert.Equal(request.EditionId, shipment.EditionId);
        Assert.Equal(request.VideoId, shipment.VideoId);
        Assert.Equal(request.CopyNumber, shipment.CopyNumber);
        Assert.Equal(request.Address, shipment.Address);
        Assert.Equal(request.Latitude, shipment.Latitude);
        Assert.Equal(request.Longitude, shipment.Longitude);
        Assert.Equal(ShipmentStatus.Preparing, shipment.Status);
        Assert.Equal(-1, shipment.LastSeq);
        Assert.Equal(Api.Micro(delivery.Clock.GetUtcNow()), shipment.CreatedAt);
        Assert.Equal("newstore warehouse, Tempelhof, Berlin", shipment.WarehouseName);
        Assert.Equal(52.4836, shipment.WarehouseLatitude);
        Assert.Equal(13.3897, shipment.WarehouseLongitude);
        Assert.Null(shipment.CourierStartedAt);
        Assert.Null(shipment.CourierFinishedAt);
    }

    [Fact]
    public async Task Repeated_ShipmentRequested_makes_one_shipment()
    {
        var request = DeliveryFixture.NewShipmentRequested(Guid.NewGuid());
        // A marker on the same key, so the same partition, after the duplicate: once it is handled, the duplicate has been read.
        var marker = DeliveryFixture.NewShipmentRequested(Guid.NewGuid());

        // The same message twice: Kafka delivers at least once, and the outbox relay may publish a row twice.
        await PublishAsync(request);
        await PublishAsync(request);
        await delivery.Kafka.PublishAsync(OrderEvents.Topic, request.OrderId.ToString(), marker, Ct);
        await DeliveryFixture.WaitAsync(() => HandledAsync(marker.EventId), "the marker is handled");

        Assert.Single(await ShipmentsOfAsync(request.OrderId));
        Assert.Equal(1, await delivery.QueryAsync(db => db.Set<InboxMessage>().CountAsync(m => m.MessageId == request.EventId, Ct)));
    }

    [Fact]
    public async Task Second_event_for_the_same_order_makes_no_second_shipment()
    {
        var first = DeliveryFixture.NewShipmentRequested(Guid.NewGuid());
        // A new message id for the same order: the inbox lets it through, and the unique order_id stops it.
        var second = first with { EventId = Guid.CreateVersion7(), Address = "Unter den Linden 1, 10117 Berlin" };

        await PublishAsync(first);
        await PublishAsync(second);
        await DeliveryFixture.WaitAsync(() => HandledAsync(second.EventId), "the second event is handled");

        var shipment = Assert.Single(await ShipmentsOfAsync(first.OrderId));
        Assert.Equal(first.Address, shipment.Address);
    }

    [Fact]
    public async Task RefundRequested_on_the_topic_is_ignored()
    {
        var refund = new RefundRequested
        {
            EventId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            OrderId = Guid.CreateVersion7(),
            PaymentId = Guid.CreateVersion7(),
        };
        var marker = DeliveryFixture.NewShipmentRequested(Guid.NewGuid());

        // Same key, same partition: once the marker is handled, the refund has been read.
        await delivery.Kafka.PublishAsync(OrderEvents.Topic, refund.OrderId.ToString(), refund, Ct);
        await delivery.Kafka.PublishAsync(OrderEvents.Topic, refund.OrderId.ToString(), marker, Ct);
        await DeliveryFixture.WaitAsync(() => HandledAsync(marker.EventId), "the marker is handled");

        Assert.False(await HandledAsync(refund.EventId));
        Assert.DoesNotContain(
            delivery.Logs.Snapshot(),
            l => l.Level >= LogLevel.Warning && l.Message.Contains(nameof(RefundRequested), StringComparison.Ordinal));
    }
}
