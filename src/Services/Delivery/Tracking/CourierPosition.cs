namespace NewStore.Delivery.Tracking;

/// <summary>
/// One position of a courier on its trip, on the stream <see cref="Topic"/> with the shipment id as the key. Produced by the courier
/// (Task 4) and consumed by the API role (Task 5). It stays in Delivery while Delivery is its only producer and consumer (AGENTS.md,
/// Streams; ADR 0022).
/// </summary>
public sealed record CourierPosition
{
    public const string Topic = "delivery.positions";

    public const int Partitions = 6;

    public required Guid ShipmentId { get; init; }

    public required int Seq { get; init; }

    /// <summary>A <see cref="Shipments.ShipmentStatus"/> name.</summary>
    public required string Status { get; init; }

    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }
}
