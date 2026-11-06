namespace NewStore.Inventory;

public sealed record ReserveRequest(Guid BuyerId);

public sealed record ReservationResponse(
    Guid Id, Guid EditionId, Guid BuyerId, string Status, DateTimeOffset ReservedAt, DateTimeOffset ExpiresAt, int? CopyNumber)
{
    public static ReservationResponse From(Reservation r, int? copyNumber = null) =>
        new(r.Id, r.EditionId, r.BuyerId, r.Status.ToString(), r.ReservedAt, r.ExpiresAt, copyNumber);
}
