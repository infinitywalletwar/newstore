using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using NewStore.Contracts.Orders;
using NewStore.Messaging;

namespace NewStore.Delivery.Shipments;

/// <summary>
/// Makes the shipment when Orders asks for one: status <c>Preparing</c>, no position yet (<c>last_seq</c> -1), and a copy of the
/// warehouse it starts from. It runs in the inbox transaction, so the row commits with the inbox row and a repeated message id never
/// reaches it. <c>ON CONFLICT (order_id) DO NOTHING</c> covers the repeat the inbox can't see: an event for an order that already has
/// its shipment, under a message id the inbox doesn't hold (a replay older than <c>Messaging:InboxRetention</c>, or a new event id from
/// Orders). It must not make a second shipment, and must not fail the message either.
/// </summary>
public sealed class ShipmentRequestedHandler(DeliveryDbContext db, IOptions<WarehouseOptions> warehouse, TimeProvider time)
    : IIntegrationEventHandler<ShipmentRequested>
{
    public async Task HandleAsync(ShipmentRequested integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var from = warehouse.Value;
        var id = Guid.CreateVersion7();
        var status = nameof(ShipmentStatus.Preparing);
        var createdAt = time.GetUtcNowToMicrosecond();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO shipments (id, order_id, buyer_id, edition_id, video_id, copy_number, address, latitude, longitude,
                warehouse_name, warehouse_latitude, warehouse_longitude, status, created_at, last_seq)
            VALUES ({id}, {integrationEvent.OrderId}, {integrationEvent.BuyerId}, {integrationEvent.EditionId}, {integrationEvent.VideoId},
                {integrationEvent.CopyNumber}, {integrationEvent.Address}, {integrationEvent.Latitude}, {integrationEvent.Longitude},
                {from.Name}, {from.Latitude}, {from.Longitude}, {status}, {createdAt}, -1)
            ON CONFLICT (order_id) DO NOTHING
            """,
            cancellationToken);
    }
}
