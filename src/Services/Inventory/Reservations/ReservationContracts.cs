namespace NewStore.Inventory;

/// <summary>
/// The caller is the trusted client <c>orders</c>: it chooses the reservation id and passes the signed-in buyer's id.
/// </summary>
public sealed record ReserveRequest(Guid ReservationId, Guid BuyerId);

public sealed record ReservationResponse(
    Guid Id, Guid EditionId, Guid BuyerId, Guid VideoId, string Format, decimal Price,
    string Status, DateTimeOffset ReservedAt, DateTimeOffset ExpiresAt, int? CopyNumber)
{
    public static ReservationResponse From(Reservation r, Edition edition, int? copyNumber = null) =>
        new(r.Id, r.EditionId, r.BuyerId, edition.VideoId, edition.Format.ToString(), edition.Price,
            r.Status.ToString(), r.ReservedAt, r.ExpiresAt, copyNumber);
}
