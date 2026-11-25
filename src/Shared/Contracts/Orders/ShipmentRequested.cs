using NewStore.Messaging;

namespace NewStore.Contracts.Orders;

/// <summary>
/// Orders asks Delivery to ship a DVD: the order is paid and Inventory confirmed the copy. The address travels with the event,
/// so Delivery never calls Orders. Published on <see cref="OrderEvents.Topic"/> with the order id as the key.
/// </summary>
public sealed record ShipmentRequested : IIntegrationEvent
{
    public required Guid EventId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required Guid OrderId { get; init; }

    public required Guid BuyerId { get; init; }

    public required Guid EditionId { get; init; }

    public required Guid VideoId { get; init; }

    public required int CopyNumber { get; init; }

    public required string Address { get; init; }

    public required double Latitude { get; init; }

    public required double Longitude { get; init; }
}
