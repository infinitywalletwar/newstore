using Microsoft.EntityFrameworkCore;

namespace NewStore.Delivery.Shipments;

/// <summary>
/// Reads a buyer's shipments for the REST API and, from Task 5, the tracking hub. Scoped. The buyer is part of every query, so
/// someone else's shipment and a missing one give the same null, and no caller can tell them apart.
/// </summary>
public sealed class ShipmentReader(DeliveryDbContext db)
{
    /// <summary>The shipment with its whole path, or null when it is missing or not <paramref name="buyerId"/>'s.</summary>
    public async Task<ShipmentDetails?> GetAsync(Guid shipmentId, Guid buyerId, CancellationToken ct)
    {
        var shipment = await db.Shipments.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == shipmentId && s.BuyerId == buyerId, ct);
        if (shipment is null)
        {
            return null;
        }

        // The primary key (shipment_id, seq) serves both the filter and the order.
        var path = await db.ShipmentPositions.AsNoTracking()
            .Where(p => p.ShipmentId == shipmentId)
            .OrderBy(p => p.Seq)
            .ToListAsync(ct);
        var positions = path
            .Select(p => new PositionResponse(p.Seq, p.Status.ToString(), p.Latitude, p.Longitude, p.RecordedAt))
            .ToList();

        return new ShipmentDetails(
            shipment.Id, shipment.OrderId, shipment.CopyNumber, shipment.VideoId, shipment.Status.ToString(), shipment.Address,
            new GeoPoint(shipment.Latitude, shipment.Longitude),
            new WarehouseResponse(shipment.WarehouseName, shipment.WarehouseLatitude, shipment.WarehouseLongitude),
            positions.Count == 0 ? null : positions[^1], positions,
            shipment.CreatedAt, shipment.PickedUpAt, shipment.DeliveredAt);
    }

    /// <summary><paramref name="buyerId"/>'s shipments, newest first (the index on buyer_id and created_at).</summary>
    public async Task<IReadOnlyList<ShipmentSummary>> ListAsync(Guid buyerId, CancellationToken ct)
    {
        var shipments = await db.Shipments.AsNoTracking()
            .Where(s => s.BuyerId == buyerId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.OrderId, s.CopyNumber, s.VideoId, s.Status, s.CreatedAt, s.DeliveredAt })   // not the address
            .ToListAsync(ct);
        return [.. shipments.Select(s =>
            new ShipmentSummary(s.Id, s.OrderId, s.CopyNumber, s.VideoId, s.Status.ToString(), s.CreatedAt, s.DeliveredAt))];
    }
}
