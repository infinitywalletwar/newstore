namespace NewStore.Inventory;

/// <summary>
/// <c>Pending</c>: just reserved, ten minutes to start paying. <c>Held</c>: the buyer is paying, so the copy is kept for
/// <see cref="InventoryOptions.HoldTtl"/>. <c>Confirmed</c>, <c>Expired</c> and <c>Released</c> are final.
/// </summary>
public enum ReservationStatus { Pending, Held, Confirmed, Expired, Released }

/// <summary>A buyer's hold on one copy of an edition, until <see cref="ExpiresAt"/>.</summary>
public sealed class Reservation
{
    /// <summary>Chosen by the caller (Orders), so a reserve whose answer was lost can be sent again (ADR 0013).</summary>
    public Guid Id { get; init; }
    public Guid EditionId { get; init; }
    public Guid BuyerId { get; init; }
    public ReservationStatus Status { get; set; }
    public DateTimeOffset ReservedAt { get; init; }

    /// <summary>When the hold began, or null while the reservation was never held.</summary>
    public DateTimeOffset? HeldAt { get; set; }

    /// <summary>
    /// <see cref="ReservedAt"/> + <see cref="InventoryOptions.ReservationTtl"/> while Pending; <see cref="HeldAt"/> +
    /// <see cref="InventoryOptions.HoldTtl"/> once Held. It moves once, at the hold, and never again.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
