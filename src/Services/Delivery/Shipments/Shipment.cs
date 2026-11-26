namespace NewStore.Delivery.Shipments;

/// <summary>
/// One DVD on its way to a buyer, made from Orders' <c>ShipmentRequested</c>: one per order. It holds the buyer's address, so only the
/// buyer may read it. The warehouse is copied in when the shipment is created, so a later change of the configured warehouse doesn't
/// move a trip that has already started.
/// </summary>
public sealed class Shipment
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid EditionId { get; set; }
    public Guid VideoId { get; set; }
    public int CopyNumber { get; set; }

    /// <summary>The destination, as the buyer entered it and placed the pin.</summary>
    public required string Address { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>The trip's start, copied from <see cref="WarehouseOptions"/> at creation.</summary>
    public required string WarehouseName { get; set; }
    public double WarehouseLatitude { get; set; }
    public double WarehouseLongitude { get; set; }

    public ShipmentStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PickedUpAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>The highest position seq stored, or -1 before the first. A position is applied only when its seq is higher.</summary>
    public int LastSeq { get; set; } = -1;
    public double? LastLatitude { get; set; }
    public double? LastLongitude { get; set; }
    public DateTimeOffset? LastPositionAt { get; set; }

    /// <summary>The courier instance that holds the lease on this shipment, until <see cref="CourierLockedUntil"/>.</summary>
    public string? CourierLockedBy { get; set; }
    public DateTimeOffset? CourierLockedUntil { get; set; }

    /// <summary>When the trip started: the timeline's zero. Set by the first claim and kept on a takeover.</summary>
    public DateTimeOffset? CourierStartedAt { get; set; }

    /// <summary>When the courier finished: the last position was acknowledged by Kafka. Until then the shipment is open.</summary>
    public DateTimeOffset? CourierFinishedAt { get; set; }
}
