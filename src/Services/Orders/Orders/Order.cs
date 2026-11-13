namespace NewStore.Orders.Orders;

/// <summary>
/// Where an order is in the checkout saga (ADR 0017). <c>Reserved</c> and <c>Paying</c> are open; <c>Completed</c>, <c>Expired</c>,
/// <c>Failed</c> and <c>Refunded</c> are final; <c>Refunding</c> waits for Payments to give the money back.
/// </summary>
public enum OrderStatus { Reserved, Paying, Completed, Expired, Failed, Refunding, Refunded }

public sealed class Order
{
    public Guid Id { get; init; }

    public Guid BuyerId { get; init; }

    public Guid EditionId { get; init; }

    /// <summary>Copied from Inventory's reservation. An edition's video, format and price never change.</summary>
    public Guid VideoId { get; init; }

    public required string Format { get; init; }

    public decimal Price { get; init; }

    /// <summary>The id Orders chose for Inventory's reservation. Orders keeps it to hold, confirm and release by id.</summary>
    public Guid ReservationId { get; init; }

    /// <summary>When Inventory will give the copy back to the shelf: the reservation's deadline, then the hold's.</summary>
    public DateTimeOffset ReservedUntil { get; set; }

    public OrderStatus Status { get; set; }

    public Guid? PaymentId { get; set; }

    public string? CheckoutUrl { get; set; }

    public int? CopyNumber { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>PostgreSQL's <c>xmin</c> system column: it changes with every update of the row.</summary>
    public uint Version { get; private set; }
}
