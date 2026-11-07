namespace NewStore.Inventory;

/// <summary>Why a reserve ended the way it did.</summary>
public abstract record ReserveOutcome
{
    public sealed record Reserved(Reservation Reservation) : ReserveOutcome;
    public sealed record SoldOut : ReserveOutcome;

    /// <summary>A Pending or Confirmed reservation exists for this buyer.</summary>
    public sealed record AlreadyHolding : ReserveOutcome;

    public sealed record NotFound : ReserveOutcome;

    /// <summary>The creator ended the sale; no copy can be reserved any more.</summary>
    public sealed record SaleEnded : ReserveOutcome;

    /// <summary>ConcurrencyToken gave up after <see cref="InventoryOptions.MaxReserveAttempts"/>.</summary>
    public sealed record Contended(int Attempts) : ReserveOutcome;
}
