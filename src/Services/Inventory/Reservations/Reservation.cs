namespace NewStore.Inventory;

public enum ReservationStatus { Pending, Confirmed, Expired, Released }

/// <summary>A buyer's hold on one copy of an edition, until <see cref="ExpiresAt"/>.</summary>
public sealed class Reservation
{
    public Guid Id { get; init; }
    public Guid EditionId { get; init; }
    public Guid BuyerId { get; init; }
    public ReservationStatus Status { get; set; }
    public DateTimeOffset ReservedAt { get; init; }

    /// <summary><see cref="ReservedAt"/> + <see cref="InventoryOptions.ReservationTtl"/>.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}
