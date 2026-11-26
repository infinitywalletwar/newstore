namespace NewStore.Delivery.Shipments;

// The REST API's and the hub's answers. JSON is camelCase, ASP.NET Core's default for minimal APIs.

public sealed record GeoPoint(double Latitude, double Longitude);

public sealed record WarehouseResponse(string Name, double Latitude, double Longitude);

public sealed record PositionResponse(int Seq, string Status, double Latitude, double Longitude, DateTimeOffset RecordedAt);

public sealed record ShipmentSummary(
    Guid Id, Guid OrderId, int CopyNumber, Guid VideoId, string Status, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);

/// <summary>
/// One shipment with its whole path. <see cref="Warehouse"/> is the shipment's own copy, as it was when the shipment was created.
/// <see cref="Path"/> is every stored position, oldest first; <see cref="LastPosition"/> is its last element, or null before the first.
/// </summary>
public sealed record ShipmentDetails(
    Guid Id, Guid OrderId, int CopyNumber, Guid VideoId, string Status, string Address,
    GeoPoint Destination, WarehouseResponse Warehouse, PositionResponse? LastPosition, IReadOnlyList<PositionResponse> Path,
    DateTimeOffset CreatedAt, DateTimeOffset? PickedUpAt, DateTimeOffset? DeliveredAt);
