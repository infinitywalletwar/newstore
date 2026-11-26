namespace NewStore.Delivery.Shipments;

/// <summary>One point of a shipment's path. The key is (shipment, seq), so a repeated position is stored once.</summary>
public sealed class ShipmentPosition
{
    public Guid ShipmentId { get; set; }
    public int Seq { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public ShipmentStatus Status { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
