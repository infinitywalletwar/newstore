namespace NewStore.Inventory;

public interface IReserveStrategy
{
    /// <summary>Takes one copy of the edition for the buyer in its own transaction, or says why not.</summary>
    Task<ReserveOutcome> ReserveAsync(Guid editionId, Guid buyerId, CancellationToken cancellationToken);
}
