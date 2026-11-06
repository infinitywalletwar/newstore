namespace NewStore.Inventory;

/// <summary>One numbered copy of an edition, owned by the buyer whose reservation was confirmed.</summary>
public sealed class Copy
{
    public Guid Id { get; init; }
    public Guid EditionId { get; init; }
    public int Number { get; init; }
    public Guid OwnerId { get; init; }
    public Guid ReservationId { get; init; }
    public DateTimeOffset AssignedAt { get; init; }
}
