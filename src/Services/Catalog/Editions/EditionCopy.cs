namespace NewStore.Catalog.Editions;

/// <summary>
/// Catalog's read copy of one Inventory edition (ADR 0011). Written only by <see cref="EditionSnapshotHandler"/>.
/// There is no foreign key to <c>videos</c>: a snapshot can arrive before its video, or for a video Catalog does not hold.
/// </summary>
public sealed class EditionCopy
{
    /// <summary>The edition's id in Inventory, not generated here.</summary>
    public Guid Id { get; init; }

    public Guid VideoId { get; init; }

    public required string Format { get; init; }

    public decimal Price { get; init; }

    public int CopiesTotal { get; init; }

    public int CopiesLeft { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>The version of the snapshot this row came from. A snapshot with a version at or below it is ignored.</summary>
    public long Version { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
