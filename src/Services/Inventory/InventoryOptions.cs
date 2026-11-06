namespace NewStore.Inventory;

public enum ReserveStrategy { ConditionalUpdate, ConcurrencyToken }

/// <summary>Bound from the <c>Inventory</c> configuration section.</summary>
public sealed class InventoryOptions
{
    public TimeSpan ReservationTtl { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(5);
    public ReserveStrategy ReserveStrategy { get; set; } = ReserveStrategy.ConditionalUpdate;

    /// <summary>Used by <see cref="ReserveStrategy.ConcurrencyToken"/> only.</summary>
    public int MaxReserveAttempts { get; set; } = 20;
}
