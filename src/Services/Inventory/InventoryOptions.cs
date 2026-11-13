namespace NewStore.Inventory;

public enum ReserveStrategy { ConditionalUpdate, ConcurrencyToken }

/// <summary>Bound from the <c>Inventory</c> configuration section.</summary>
public sealed class InventoryOptions
{
    public TimeSpan ReservationTtl { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>
    /// How long a Held reservation keeps its copy, counted from the hold. Stripe's Checkout Session may live up to 24 hours
    /// and at least 30 minutes; 45 minutes leaves room for a slow payment (ADR 0013).
    /// </summary>
    public TimeSpan HoldTtl { get; set; } = TimeSpan.FromMinutes(45);

    public TimeSpan ExpirySweepInterval { get; set; } = TimeSpan.FromSeconds(5);
    public ReserveStrategy ReserveStrategy { get; set; } = ReserveStrategy.ConditionalUpdate;

    /// <summary>Used by <see cref="ReserveStrategy.ConcurrencyToken"/> only.</summary>
    public int MaxReserveAttempts { get; set; } = 20;

    /// <summary>How often the snapshot pass looks for editions that changed (ADR 0011).</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The most editions one pass sends.</summary>
    public int SnapshotBatchSize { get; set; } = 500;
}
