namespace NewStore.Inventory;

public interface IReserveStrategy
{
    /// <summary>
    /// Takes one copy of the edition for the buyer in its own transaction, or says why not. The caller chose
    /// <paramref name="reservationId"/>: sending the same request again returns the reservation it made (<see cref="ReserveOutcome.Existing"/>).
    /// </summary>
    Task<ReserveOutcome> ReserveAsync(
        Guid reservationId, Guid editionId, Guid buyerId, CancellationToken cancellationToken);
}
